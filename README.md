::: {align="center"}
[Português (Brasil)](README.ptbr.md) · **English**

# Contador de Bau

[![Windows 10 \|
11](https://img.shields.io/badge/Windows-10%20%7C%2011-3a7bd5?style=flat-square)](https://www.microsoft.com/windows)
[![.NET
10](https://img.shields.io/badge/.NET-10-512bd4?style=flat-square)](https://dotnet.microsoft.com/)
[![MIT
License](https://img.shields.io/badge/license-MIT-blue?style=flat-square)](LICENSE)

**Track the chests and treasures found in your Fortnite matches.**
:::

## Overview

Contador de Bau (the repository and namespaces are still identified
internally as **ChestCounter**) is a Windows desktop application that
analyzes Fortnite replay files and records the chests and treasures
identified in each match.

The application keeps a local match history and presents statistics
through a desktop interface, reports, and an overlay. Replay reading is
provided by libraries included in this repository.

-   **Dashboard:** totals, recent match, statistics, records, treasures,
    and charts.
-   **Reports:** match and statistics summaries, including chests,
    treasures, and teammates where supported by the available data.
-   **Overlay:** on-screen counters and integration with an OBS browser
    source.
-   **Profiles:** separate statistics when replays from multiple
    Fortnite accounts are found on the same computer.
-   **Local storage:** match history and settings remain on the
    computer.
-   **Privacy:** the application analyzes replay files; it does not need
    to read or modify Fortnite process memory.

> Counts depend on the information actually present in replay files. The
> application does not reveal unopened chest locations on the map.

## Requirements

-   Windows 10 or Windows 11.
-   For a published release: download the executable from the Releases
    page.
-   For development: Git and the .NET 10 SDK.
-   Fortnite replay recording must be enabled.

## Getting started

1.  Download the available release from [GitHub
    Releases](../../releases/latest).
2.  In Fortnite, enable **Settings → Game → Replays → Record Replays**.
    Enable large-team replay recording if relevant to your game modes.
3.  Run `ContadorDeBau.exe` (the intended executable name after the
    project configuration is updated).
4.  On first launch, follow the available options to import existing
    replays or start counting future matches.
5.  Keep the application running while playing. The window can be
    minimized.

Counting depends on Fortnite recording the match. Matches played while
the application is closed may be processed later if their replay files
are still available.

## Features

### Dashboard

The dashboard presents the available statistics, such as totals, the
current or most recent match, session data, records, averages, recent
activity, and team information when enabled.

### Reports

The application generates local reports containing match and statistics
information. The details shown depend on the data extracted from replays
and the enabled settings.

The dashboard's Report button opens the matching FortCards player profile
in the default browser. If the profile cannot be identified safely or the
API is unavailable, it opens the general player list instead.

### Teammates and team

When teammate tracking is enabled, the application can display
information about matches played together and teammate chest counts
observed in replay files.

Teammate chest counts should be treated as approximate. A replay may not
record all activity from players outside the observation range of the
local player.

### Overlay and OBS

The overlay displays chest and treasure counters. OBS integration uses a
local browser page served by the application itself.

To display the overlay over Fortnite, **Windowed Fullscreen** may be
required because Windows does not reliably draw windows over exclusive
fullscreen mode.

Check the application settings for the local overlay address and add it
to OBS as a browser source.

### Settings, profiles, and updates

The application provides settings for language, replay files, overlay,
reports, profiles, and other options available in the installed version.
Update behavior depends on the preferences and implementation in that
version.

## How it works

1.  Fortnite records replay files for matches.
2.  Contador de Bau monitors the configured folder and detects new
    replays.
3.  Replay-reading and analysis components extract available events and
    statistics.
4.  Results are saved locally and used by the dashboard and reports.

The application does not read, inject into, or modify Fortnite process
memory, and it does not alter the game's installation files.

The usual Fortnite replay folder is:

``` text
%LOCALAPPDATA%\FortniteGame\Saved\Demos
```

The effective location may be changed in the application settings when
that option is available.

## Local data, backups, and Replay API

Match history remains stored locally. When Replay API synchronization is
enabled, a completed parser snapshot is also placed in a local retry queue
and submitted to the endpoint in `reference/openapi.yaml`. API outages do
not interrupt local analysis.

Application data is stored in the data directory used by the
application. The exact path and filenames may vary by version and
startup arguments. Back up the data before moving to another computer,
reinstalling, or performing manual maintenance.

The API configuration is `api-config.json` in the data directory. It
supports `enabled`, `baseUrl`, and `applicationId`. ChestCounter requests
a token from `POST /auth/mock` at the configured base URL, retains it only
in memory, and refreshes it when necessary. No JWT secret is stored or
generated by ChestCounter.

The retry queue is stored in `api-sync-queue.json`. Retries keep the same
idempotency key. Entries using the former normalized payload are retained
as blocked; replay them again to create a snapshot using the current
contract.

The client sends a parser snapshot to the ingestion endpoint without a
separate file-registration request. Deployed API versions must support the
snapshot contract in `reference/openapi.yaml`.

Replay ingestion uses the same general Bearer JWT as other protected API
routes. ChestCounter obtains it from `/auth/mock` without generating a token
or adding claims. In the API source, `/auth/mock` is available only in
development; production synchronization requires a deployment with an
appropriate existing token-issuance flow supported by the client.

The current parser snapshot also has strict-schema differences that must
be aligned before the API can accept the complete data object:

- `match.gameData` emits `maxPlayers`, `totalTeams`, `totalBots`,
  `tournamentRound`, and `isLargeTeamGame`, which are not declared.
- Game-state observations emit `replicatedWorldTimeSeconds`,
  `replicatedWorldTimeSecondsDouble`, and `hasBegunPlay`; the schema
  declares `worldTimeSeconds` instead of the two time fields.
- Match/team statistics and players emit `info`, `unknown` in team stats,
  and `inventoryId`, which are not declared.
- Cosmetics contain additional fields and names that differ from the
  schema; movement emits `serverPhysicsHandle` where the schema declares
  `handle`.
- Map flight paths use different property names and provide scalar flight
  speed where the schema expects a velocity vector. The parser also emits
  tag-container objects for `poiTagContainerTable`, while the schema
  specifies strings.
- Equipped-item observations emit `item` instead of `name`, and
  prize-handler grants include `elementId`, which the schema does not
  specify.

These values are not silently discarded or inferred by the client. The
parser and API schema need an explicit alignment decision before strict
payload validation can pass.

## Command-line options

The application includes argument handling for auxiliary operations such
as selecting directories and processing replays. Refer to `Program.cs`
in the matching source version for the exact supported parameters. Do
not assume command-line options from older versions are still available.

## Repository structure

``` text
.
├── .github/
│   └── workflows/                  # GitHub Actions automation
├── docs/
│   ├── architecture/               # Architecture documentation
│   └── development/                # Development guidance
├── lib/                            # Replay-reading related libraries
├── src/
│   ├── ChestCounter/               # Avalonia desktop application (App)
│   │   ├── Assets/
│   │   ├── Localization/
│   │   ├── Platform/Windows/
│   │   ├── Services/
│   │   ├── ViewModels/
│   │   └── Views/
│   ├── ChestCounter.Application/   # Use cases and application contracts
│   ├── ChestCounter.Core/          # Domain and core rules
│   └── ChestCounter.Infrastructure/# Technical implementations
├── tools/                          # Development utilities
├── build.bat
├── Directory.Build.props
├── LICENSE
├── README.md
└── README.ptbr.md
```

Folders such as `bin`, `obj`, and `dist` are local build/distribution
artifacts, not architectural layers.

## Architecture

The source code is organized into four .NET projects:

  -----------------------------------------------------------------------
  Project                             Responsibility
  ----------------------------------- -----------------------------------
  `ChestCounter`                      Avalonia desktop application, UI,
                                      ViewModels, assets, and startup. It
                                      acts as the composition root.

  `ChestCounter.Application`          Use cases, operation orchestration,
                                      and application contracts.

  `ChestCounter.Core`                 Domain entities, definitions, and
                                      core rules, independent of the UI.

  `ChestCounter.Infrastructure`       Technical implementations, local
                                      persistence, replay processing,
                                      reporting, and integrations.
  -----------------------------------------------------------------------

### Dependency direction

The intended project dependency direction is:

-   `ChestCounter` references `ChestCounter.Application`,
    `ChestCounter.Core`, and `ChestCounter.Infrastructure`.
-   `ChestCounter.Application` references only `ChestCounter.Core`.
-   `ChestCounter.Infrastructure` references `ChestCounter.Application`
    and `ChestCounter.Core`.
-   `ChestCounter.Core` does not reference the other internal projects.

Application defines contracts; Infrastructure implements them; the
desktop project wires concrete implementations during startup.
Application must not depend on Infrastructure.

Responsibility separation is still in progress. A file being located in
a project does not by itself prove that all of its dependencies have
been fully decoupled. See `docs/architecture/` for more detailed
architecture notes.

### Replay API integration status

The client uses the existing Replay parser output, computes the Replay
file SHA-256 from the original bytes, and queues idempotent submissions
after local analysis succeeds. The API payload and endpoint follow
`reference/openapi.yaml`; the parser/schema differences listed above remain
blockers to end-to-end acceptance.

## Development

### Set up the environment

1.  Install Git.
2.  Install the .NET 10 SDK.
3.  Clone the repository:

``` bash
git clone https://github.com/c3bola/ChestCounter.git
cd ChestCounter
```

4.  Open the projects in an IDE that supports .NET 10 and Avalonia.

The repository includes `build.bat` and a workflow under
`.github/workflows/`. Review those files for the current build and
distribution paths and procedures. The desktop project retains the
internal name `ChestCounter`; the intended product/executable name is
`ContadorDeBau`.

### Projects and references

Each project is an independent .NET project with its own `.csproj` file.
When moving or adding files, check:

-   Project references (`ProjectReference`).
-   Explicit source inclusions (`Compile Include`), if used.
-   Avalonia resources (`.axaml`), images, and fonts.
-   `EmbeddedResource` and other resource declarations.
-   `AssemblyName`, `RootNamespace`, and publishing settings.

Do not compile the same source file into multiple assemblies. Do not
introduce an Application-to-Infrastructure reference.

### Additional documentation

-   `docs/architecture/` --- architecture overview, replay processing,
    and overlay.
-   `docs/development/` --- environment setup and testing guidance.
-   `lib/PATCHES.md` --- changes applied to the related libraries.

## Contributing and issue reports

Bug reports and suggestions are welcome through [GitHub
Issues](../../issues).

For replay-reading problems, include the error message and relevant
application log when available. Avoid publishing replay files or
personal data without reviewing what they contain.

## License and credits

This project is distributed under the [MIT License](LICENSE).
Replay-reading and decompression libraries may have their own notices
and terms; review the relevant license files and
[`lib/PATCHES.md`](lib/PATCHES.md).

This project is not affiliated with, endorsed, or sponsored by Epic
Games. Fortnite is a registered trademark of Epic Games, Inc.
