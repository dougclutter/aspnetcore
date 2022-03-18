# Modified Microsoft.Authentication.WebAssembly.Msal
Problem statement:
- Creating a .NET 6 Blazor WASM project
- Using Azure Active Directory B2C directory for authentication
- Was unable to use **Microsoft.Authentication.WebAssembly.Msal** package to start Registration
and Profile user flows

## Caveat
This may be supported by MS, but I was unable to find a way to make it work...even after
crawling through the source code.  Of course, this doesn't mean it *isn't* support,
it just means I couldn't find it.

## Work-around
I forked the aspnetcore repo for .NET 6.0.2 on github to https://github.com/dougclutter/aspnetcore.
Next, I created a SLNF file that included the **Microsoft.Authentication.WebAssembly.Msal** package
and all it's dependencies (13 projects in all).

According to the B2C documentation, you start a user flow by inititiating a sign in
with the authority set to the user flow.  For example:
- B2C directory name is **myB2C**
- Sign up user flow named **B2C_1_Signup**
- Authority to start this user flow would be **https://myB2C.b2clogin.com/myB2C.com/B2C_1_Signup**

## The Problem
There's no discernable way to specify the authority *for the register/profile* options.
Microsoft allows you to configure B2C in the appsettings.json file as follows:
```
{
  "AzureAdB2C": {
    "Authority": "https://myB2C.b2clogin.com/myB2C.com/B2C_1_Signin",
    "ClientId": "be15af53-b152-43ee-8a61-42d48f06e44d",
    "ValidateAuthority": false
  }
}
```

This works great if you only want to use a *single* user flow, but it falls apart quickly if you
want to use multiple user flows.  They *do* allow you to add a *local* URL for register and profile
using these settings:
```
{
  "AzureAdB2C": {
    "Authority": "https://myB2C.b2clogin.com/myB2C.com/B2C_1_Signin",
    "ClientId": "be15af53-b152-43ee-8a61-42d48f06e44d",
    "ValidateAuthority": false,
    "RemoteProfilePath": "/myLocalProfilePath",
    "RemoteRegisterPath": "/myLocalRegisterPath"
  }
}
```

But this doesn't really help us with all the complicated handshakes needed to authenticate
against B2C, so I'm not sure what they were thinking when they added this.

So, after banging my head against this, I decided to just change things to solve the problem.
Isn't open source fun?

## My Horrible Hack
First, I had to find someplace to store the authorities in their settings.
They already have an array called KnownAuthorities, so I just repurposed it for my needs:
```
{
  "AzureAdB2C": {
    "Authority": "https://myB2C.b2clogin.com/myB2C.com/B2C_1_Signin",
    "ClientId": "be15af53-b152-43ee-8a61-42d48f06e44d",
    "ValidateAuthority": false,
    "KnownAuthorities": [
      "https://myB2C.b2clogin.com/myB2C.com/B2C_1_Signin",
      "https://myB2C.b2clogin.com/myB2C.com/B2C_1_Signup",
      "https://myB2C.b2clogin.com/myB2C.com/B2C_1_Profile"
    ]
  }
}
```

Next, I had to modify their **RemoteAuthenticatorViewCore** to pass the authority that I
wanted to use for each operation:
```
        protected override async Task OnParametersSetAsync()
        {
            switch (Action)
            {
                case RemoteAuthenticationActions.LogIn:
                    await ProcessLogIn(GetReturnUrl(state: null), 0);
                    break;
                case RemoteAuthenticationActions.LogInCallback:
                    await ProcessLogInCallback();
                    break;
                case RemoteAuthenticationActions.LogInFailed:
                    break;
                case RemoteAuthenticationActions.Profile:
                    await ProcessLogIn(GetReturnUrl(state: null), 2);
                    break;
                case RemoteAuthenticationActions.Register:
                    await ProcessLogIn(GetReturnUrl(state: null), 1);
                    break;
                case RemoteAuthenticationActions.LogOut:
                    await ProcessLogOut(GetReturnUrl(state: null, Navigation.ToAbsoluteUri(ApplicationPaths.LogOutSucceededPath).AbsoluteUri));
                    break;
                case RemoteAuthenticationActions.LogOutCallback:
                    await ProcessLogOutCallback();
                    break;
                case RemoteAuthenticationActions.LogOutFailed:
                    break;
                case RemoteAuthenticationActions.LogOutSucceeded:
                    break;
                default:
                    throw new InvalidOperationException($"Invalid action '{Action}'.");
            }
        }

```
Note that I'm just passing in the index of the authority I want to use for the corresponding
operation.  After that, it was pretty easy to modify the entire stack so it used the index.
