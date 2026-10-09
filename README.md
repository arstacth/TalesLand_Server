# TalesLand Server Emulator 『 テイルズランド』

<img width="800" height="600" alt="20261006231047" src="https://github.com/user-attachments/assets/2c2d41c6-2c56-43ca-ad48-7c9303b525d0" />
<img width="800" height="600" alt="20261009041021" src="https://github.com/user-attachments/assets/a67607ab-07f2-4568-8560-d140c4ad9dec" />
<img width="800" height="600" alt="20261006222335" src="https://github.com/user-attachments/assets/25edc536-f4eb-42eb-bea3-fc974b10de03" />

This project (the `TR_SERVER` solution) lets you run that on your own machine. A client talks to three programs:

1. **LoadBalanceServer** checks the client and tells it where the game server is.
2. **AgentServer** handles login, characters, items, rooms, and the operator window.
3. **RelayServer** forwards UDP traffic between players while they are in a session.


### Saved data

Accounts and the game catalog are in the MySQL database `talesland_db`. That catalog includes characters, items, maps, quests, farm content, shops, capsule machines, and emblems.

- `database/make_db.sql` creates the empty database.
- `database/talesland_db.sql` loads the schema and the catalog data.

## Programs

| Program | Role |
| --- | --- |
| `LoadBalanceServer` | First TCP stop. Reads `settings.ini`, checks the client hash when `HashCheck` is true, then returns the agent address and port. |
| `AgentServer` | Game session server and operator window. Needs `hash.ini`, `settings.ini`, and `bin\TRServer.dll` beside the executable. |
| `RelayServer` | UDP relay on port **9503**. Players register, heartbeat, and forward packets through it. |
| `LocalCommons` | Shared library used by the servers. |

A local `settings.ini` uses load-balancer port **9500**, agent port **9153**, and MySQL on `127.0.0.1`. Those values come from `settings.ini`, which stays in the local `bin` folder and is not part of this repository.

## What is in this repository

```
AgentServer/          game server and operator window
LoadBalanceServer/    client entry and hash check
RelayServer/          UDP relay
LocalCommons/         shared library
database/             MySQL schema and catalog
packages/             libraries the projects compile against
tools/                build helpers
TR_SERVER.sln         Visual Studio solution
BUILD.bat             release rebuild
```

## Build

The projects target **.NET Framework 4.8** on Windows. Build them with Visual Studio 2019 or 2022, or run `BUILD.bat`, which finds MSBuild and rebuilds `TR_SERVER.sln` in Release. The executables are written to `bin\`.

MySQL must already be running, and `talesland_db` must be loaded, before AgentServer can log players in.
