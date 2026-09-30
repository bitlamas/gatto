```
  /l、
（＾､＾７
  l  ~ヽ
  じしf_,)ノ
```

# gatto

the AI assistant that runs on your own PC.

![.NET 10](https://img.shields.io/badge/.NET-10-512BD4)
![Windows 10 / 11](https://img.shields.io/badge/platform-Windows%2010%20%2F%2011-0078D4)
[![GPL-3.0](https://img.shields.io/github/license/bitlamas/gatto)](LICENSE)

gatto is a terminal-based LLM harness built with local models in mind. Even if you know nothing about running a model, gatto helps you:

- identify what hardware you have
- pick the right build of the engine (llama.cpp)
- pick a model that fits your hardware
- test that model, to make sure it can use gatto's tools

From there gatto gives the model real hands: running commands, searching the web, reading and writing files. You get a field engineer living in your terminal, working through your projects with you. Even if your PC is on the older side, gatto tries to be especially good with small models: the ones that need less hardware and are less capable at complex tasks.

A (large language) model is a file on your own hard drive. Everything you type and every file gatto opens with a local model stays on your machine. No account, no subscription, no telemetry of any kind.

> **Windows only.** Built, tested and walked end to end on Windows before every release. The core is platform-neutral and GPL-3.0.

## quick start

Download the [latest release](https://github.com/bitlamas/gatto/releases/latest), unzip `gatto.exe`, and either double-click it or run it from a terminal:

```powershell
gatto setup
```

Windows may complain because the .exe is not code-signed. Click **More info → Run anyway**.

The wizard walks you through five named steps: it identifies your machine, suggests an engine, shows you the model hub where you download a model, checks that the model works, and then it is done. To try other models later, run `gatto model` at a shell prompt (not inside a gatto session); it opens the same wizard at its model step.

Then:

```powershell
gatto            # the REPL, generalist role
gatto coder      # or a role by name
gatto model      # add a model
gatto doctor     # read-only health checks; paste its output into any issue
gatto uninstall  # when you don't want to play with gatto anymore
```

Everything lives under `~\.gatto\` (override with `GATTO_HOME`). The full manual to point your AI assistant to is at [gatto.computer/manual](https://gatto.computer/manual/).

## verify the download

There is no install script, and there will not be one. You download a file, you can check it before it runs, and you decide where it lives.

Every new version is a zip and its SHA256 is published with the release:

```powershell
Get-FileHash .\gatto-*-win-x64.zip -Algorithm SHA256
```

`gatto update` uses the same channel. It tells you if a new version is found, asks before updating, and verifies the download against the digest GitHub publishes for the asset.

## permissions

By default, anything on gatto that runs a command or writes a file asks you first. `/wild` turns the prompts and the commit checkpoint off for the session, and **Shift+Tab** toggles it too; `/wild always` or `/wild never` makes it stick per project. `/tools` lists what the model can use this session.

## extensions

An extension is a small C# script, a `.csx` file under `~\.gatto\extensions\`, compiled at launch and available to every project. `ask_user` and `web_search` ship this way. The shipped ones live in [gatto-extensions](https://github.com/bitlamas/gatto-extensions); writing your own is described at [gatto.computer/extensions/write](https://gatto.computer/extensions/write/). Still a work in progress.

## roles

A role is a seat for gatto: a small JSON file that can name a model, append a prompt, arm a check or two, and decide whether commits pause for you. gatto ships three roles: `generalist`, `coder` and `oracle`. You can add new roles, of course: drop `reviewer.json` into `~\.gatto\roles\` and `gatto reviewer` works. More at [gatto.computer/roles](https://gatto.computer/roles/). Also a work in progress.

## llama.cpp serving

`gatto serve` runs `llama-server.exe` for you, from the model's own settings:

```powershell
gatto serve start <model>   # spawns, polls /health, records pid+port
gatto serve status
gatto serve stop
```

When you start a session, gatto launches the engine for you. When you close gatto, the engine keeps running by default: run `gatto serve stop` to eject the model, or set `"stop_server_on_exit": true` in `gatto.json`.

## building from source

```powershell
git clone https://github.com/bitlamas/gatto.git
cd gatto
dotnet build Gatto -o bin/stable       # bin\stable\gatto.exe
dotnet test --nologo -o tmp/testbin    # the full suite
```

`gatto --version` prints the exact build.

## status

Beta. Used daily by one person (me) against a handful of local models. Found something broken? [Open an issue](https://github.com/bitlamas/gatto/issues) with `gatto doctor`'s output.

## license

GPL-3.0. See [LICENSE](LICENSE).
