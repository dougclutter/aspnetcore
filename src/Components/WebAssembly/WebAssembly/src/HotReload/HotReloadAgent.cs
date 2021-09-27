// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Based on the implementation in https://raw.githubusercontent.com/dotnet/sdk/4a0473b29bfd4c7bdda6080d821788ee6d50c86b/src/BuiltInTools/DotNetDeltaApplier/HotReloadAgent.cs

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;

namespace Microsoft.Extensions.HotReload
{
    internal sealed class HotReloadAgent : IDisposable
    {
        private readonly Action<string> _log;
        private readonly AssemblyLoadEventHandler _assemblyLoad;
        private readonly ConcurrentDictionary<Guid, List<UpdateDelta>> _deltas = new();
        private readonly ConcurrentDictionary<Assembly, Assembly> _appliedAssemblies = new();
        private volatile UpdateHandlerActions? _handlerActions;

        public HotReloadAgent(Action<string> log)
        {
            _log = log;
            _assemblyLoad = OnAssemblyLoad;
            AppDomain.CurrentDomain.AssemblyLoad += _assemblyLoad;
        }

        private void OnAssemblyLoad(object? _, AssemblyLoadEventArgs eventArgs)
        {
            _handlerActions = null;
            var loadedAssembly = eventArgs.LoadedAssembly;
            var moduleId = TryGetModuleId(loadedAssembly);
            if (moduleId is null)
            {
                return;
            }

            if (_deltas.TryGetValue(moduleId.Value, out var updateDeltas) && _appliedAssemblies.TryAdd(loadedAssembly, loadedAssembly))
            {
                // A delta for this specific Module exists and we haven't called ApplyUpdate on this instance of Assembly as yet.
                ApplyDeltas(loadedAssembly, updateDeltas);
            }
        }

        internal sealed class UpdateHandlerActions
        {
            public List<Action<Type[]?>> ClearCache { get; } = new();
            public List<Action<Type[]?>> UpdateApplication { get; } = new();
        }

        private UpdateHandlerActions GetMetadataUpdateHandlerActions()
        {
            Console.WriteLine("Begin GetMetadataUpdateHandlerActions");

            // We need to execute MetadataUpdateHandlers in a well-defined order. For v1, the strategy that is used is to topologically
            // sort assemblies so that handlers in a dependency are executed before the dependent (e.g. the reflection cache action
            // in System.Private.CoreLib is executed before System.Text.Json clears it's own cache.)
            // This would ensure that caches and updates more lower in the application stack are up to date
            // before ones higher in the stack are recomputed.
            var sortedAssemblies = TopologicalSort(AppDomain.CurrentDomain.GetAssemblies());
            var handlerActions = new UpdateHandlerActions();
            foreach (var assembly in sortedAssemblies)
            {
                foreach (var attr in assembly.GetCustomAttributesData())
                {
                    // Look up the attribute by name rather than by type. This would allow netstandard targeting libraries to
                    // define their own copy without having to cross-compile.
                    if (attr.AttributeType.FullName != "System.Reflection.Metadata.MetadataUpdateHandlerAttribute")
                    {
                        continue;
                    }

                    IList<CustomAttributeTypedArgument> ctorArgs = attr.ConstructorArguments;
                    if (ctorArgs.Count != 1 ||
                        ctorArgs[0].Value is not Type handlerType)
                    {
                        _log($"'{attr}' found with invalid arguments.");
                        continue;
                    }

                    GetHandlerActions(handlerActions, handlerType);
                }
            }

            return handlerActions;
        }

        internal void GetHandlerActions(UpdateHandlerActions handlerActions, Type handlerType)
        {
            bool methodFound = false;

            if (GetUpdateMethod(handlerType, "ClearCache") is MethodInfo clearCache)
            {
                handlerActions.ClearCache.Add(CreateAction(clearCache));
                methodFound = true;
            }

            if (GetUpdateMethod(handlerType, "UpdateApplication") is MethodInfo updateApplication)
            {
                handlerActions.UpdateApplication.Add(CreateAction(updateApplication));
                methodFound = true;
            }

            if (!methodFound)
            {
                _log($"No invokable methods found on metadata handler type '{handlerType}'. " +
                    $"Allowed methods are ClearCache, UpdateApplication");
            }


            Action<Type[]?> CreateAction(MethodInfo update)
            {
                Console.WriteLine($"Found handler {update}");

                Action<Type[]?> action = update.CreateDelegate<Action<Type[]?>>();
                return types =>
                {
                    try
                    {
                        Console.WriteLine($"Calling metadataupdate handler {update}.");
                        action(types);
                    }
                    catch (Exception ex)
                    {
                        _log($"Exception from '{action}': {ex}");
                    }
                };
            }

            MethodInfo? GetUpdateMethod(Type handlerType, string name)
            {
                if (handlerType.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, new[] { typeof(Type[]) }) is MethodInfo updateMethod &&
                    updateMethod.ReturnType == typeof(void))
                {
                    return updateMethod;
                }

                foreach (MethodInfo method in handlerType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                {
                    if (method.Name == name)
                    {
                        _log($"Type '{handlerType}' has method '{method}' that does not match the required signature.");
                        break;
                    }
                }

                return null;
            }
        }

        internal static List<Assembly> TopologicalSort(Assembly[] assemblies)
        {
            Console.WriteLine("Topoligically sorting assemblies before discovering metadata update handlers.");
            var sortedAssemblies = new List<Assembly>(assemblies.Length);

            var visited = new HashSet<string>(StringComparer.Ordinal);

            foreach (var assembly in assemblies)
            {
                Visit(assemblies, assembly, sortedAssemblies, visited);
            }

            static void Visit(Assembly[] assemblies, Assembly assembly, List<Assembly> sortedAssemblies, HashSet<string> visited)
            {
                Console.WriteLine($"Visiting {assembly}.");

                var assemblyIdentifier = assembly.GetName().Name!;
                if (!visited.Add(assemblyIdentifier))
                {
                    return;
                }

                foreach (var dependencyName in assembly.GetReferencedAssemblies())
                {
                    var dependency = Array.Find(assemblies, a => a.GetName().Name == dependencyName.Name);
                    if (dependency is not null)
                    {
                        Visit(assemblies, dependency, sortedAssemblies, visited);
                    }
                }

                sortedAssemblies.Add(assembly);
            }

            return sortedAssemblies;
        }

        public void ApplyDeltas(IReadOnlyList<UpdateDelta> deltas)
        {
            Console.WriteLine("Called ApplyDeltas");

            try
            {
                for (var i = 0; i < deltas.Count; i++)
                {
                    var item = deltas[i];
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (TryGetModuleId(assembly) is Guid moduleId && moduleId == item.ModuleId)
                        {
                            Console.WriteLine($"Calling ApplyUpdate on {assembly}.");
                            MetadataUpdater.ApplyUpdate(assembly, item.MetadataDelta, item.ILDelta, ReadOnlySpan<byte>.Empty);
                        }
                    }

                    // Additionally stash the deltas away so it may be applied to assemblies loaded later.
                    var cachedDeltas = _deltas.GetOrAdd(item.ModuleId, static _ => new());
                    cachedDeltas.Add(item);
                }

                Type[]? updatedTypes = GetMetadataUpdateTypes(deltas);

                 // Defer discovering the receiving deltas until the first hot reload delta.
                // This should give enough opportunity for AppDomain.GetAssemblies() to be sufficiently populated.
                _handlerActions ??= GetMetadataUpdateHandlerActions();
                var handlerActions = _handlerActions;

                Console.WriteLine($"Calling metadataupdate handlers.");
                handlerActions.ClearCache.ForEach(a => a(updatedTypes));
                handlerActions.UpdateApplication.ForEach(a => a(updatedTypes));

                _log("Deltas applied.");
            }
            catch (Exception ex)
            {
                _log(ex.ToString());
            }
        }

        [UnconditionalSuppressMessage("ReflectionAnalysis", "IL2026:RequiresUnreferencedCode",
            Justification = "OpenComponent already has the right set of attributes")]
        private Type[] GetMetadataUpdateTypes(IReadOnlyList<UpdateDelta> deltas)
        {
            List<Type>? types = null;

            foreach (var delta in deltas)
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => TryGetModuleId(assembly) is Guid moduleId && moduleId == delta.ModuleId);
                if (assembly is null)
                {
                    continue;
                }

                var assemblyTypes = assembly.GetTypes();

                foreach (var updatedType in delta.UpdatedTypes ?? Array.Empty<int>())
                {
                    var type = assemblyTypes.FirstOrDefault(t => t.MetadataToken == updatedType);
                    if (type != null)
                    {
                        types ??= new();
                        types.Add(type);
                    }
                }
            }

            return types?.ToArray() ?? Type.EmptyTypes;
        }

        public void ApplyDeltas(Assembly assembly, IReadOnlyList<UpdateDelta> deltas)
        {
            try
            {
                // Defer discovering the receiving deltas until the first hot reload delta.
                // This should give enough opportunity for AppDomain.GetAssemblies() to be sufficiently populated.
                _handlerActions ??= GetMetadataUpdateHandlerActions();
                var handlerActions = _handlerActions;

                foreach (var item in deltas)
                {
                    MetadataUpdater.ApplyUpdate(assembly, item.MetadataDelta, item.ILDelta, ReadOnlySpan<byte>.Empty);
                }

                _log("Deltas applied.");
            }
            catch (Exception ex)
            {
                _log(ex.ToString());
            }
        }

        public void Dispose()
        {
            AppDomain.CurrentDomain.AssemblyLoad -= _assemblyLoad;
        }

        private static Guid? TryGetModuleId(Assembly loadedAssembly)
        {
            try
            {
                return loadedAssembly.Modules.FirstOrDefault()?.ModuleVersionId;
            }
            catch
            {
                // Assembly.Modules might throw. See https://github.com/dotnet/aspnetcore/issues/33152
            }

            return default;
        }
    }
}
