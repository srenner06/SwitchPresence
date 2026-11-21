# SwitchPresence

Shows the game you're playing on your **Nintendo Switch 2** (or Switch 1) as your Discord activity — automatically, with no manual game picking.

A small Windows app: a console you can start and watch, plus a windowless build for autostart.

> **Status: in development. Nothing works yet.**
> The Nintendo protocol is being verified against live servers. Don't expect a usable release until this notice is gone.

## How it works

Nintendo's API does not expose *your own* presence — only your friends'. So this uses the approach every automatic tool in this space uses:

1. You sign in with a **second, throwaway Nintendo Account** that is friends with your main account.
2. The app reads your main account's presence out of that second account's friend list.
3. It maps the current game to a Discord activity over Discord's local pipe.

Your main account is never signed in to, and your Switch is never modified. No custom firmware, no capture card, no extra hardware.

Requests to Nintendo's Coral API must be signed and encrypted by code that only exists inside the real Nintendo Switch Online Android app. This project does not reimplement that — it uses the community-hosted [**nxapi-znca-api**](https://github.com/samuelthomas2774/nxapi-znca-api) service, as required to talk to Coral at all.

## Privacy — read this before you set it up

To generate those signatures, the second account's Nintendo **`id_token`**, its **Coral token**, and **all data sent to and received from the Coral API** are sent to a third-party service (nxapi-znca-api), operated independently of this project.

That is why this app requires a **second, disposable Nintendo Account** and will refuse to help you sign in with your main one. The app asks you to acknowledge this explicitly before the first sign-in.

Using an unofficial Nintendo API is against Nintendo's terms of service. There are no known bans for presence-only use, but the risk is yours, which is another reason the second account should be one you don't mind losing.

## Requirements

- Windows, with Discord running on the same PC
- A second Nintendo Account, friends with your main account
- Your main account sharing its online status with that friend
  (Switch → System Settings → Users → *your user* → Friend Settings; if sharing is limited to Best Friends, mark the second account as a Best Friend)
- A free [nxapi-auth](https://nxapi-auth.fancy.org.uk/oauth/clients) client ID — only if you're building from source; releases ship with one

## Building

```powershell
dotnet build
```

Requires the .NET 10 SDK. Release builds publish as NativeAOT, so no runtime install is needed to run them.

## Credits

- [**nxapi**](https://github.com/samuelthomas2774/nxapi) by Samuel Elliott — the reference implementation this project's protocol handling was written from. No nxapi code is used here; nxapi is AGPL-3.0, this project is not a derivative of it.
- [**nxapi-znca-api**](https://github.com/samuelthomas2774/nxapi-znca-api) — the f-generation, request-encryption and response-decryption service this app depends on.

Not affiliated with, endorsed by, or connected to Nintendo. "Nintendo Switch" is a trademark of Nintendo.

## License

MIT — license file to follow before the first release.
