#!/usr/bin/env -S dotnet fsi --
#r "nuget: Fun.Build, 1.1.18"

open System
open System.IO
open Fun.Build

let root = __SOURCE_DIRECTORY__

/// A shallow clone of dotnet/fsharp main. It is git-ignored; the docs are generated from its
/// `docs` folder and from the FSharp.Compiler.Service it builds.
let fsharpDir = Path.Combine(root, "fsharp")

/// The SDK bootstrap script dotnet/fsharp ships. It runs its arguments with the exact SDK that
/// `fsharp/global.json` asks for, installing it under `fsharp/.dotnet` first when this machine
/// does not have it. Building through it means nobody has to keep a preview SDK on their PATH.
let upstreamDotnet =
    if OperatingSystem.IsWindows() then
        let script = Path.Combine(fsharpDir, "eng", "common", "dotnet.cmd")
        $"cmd /c \"{script}\""
    else
        Path.Combine(fsharpDir, "eng", "common", "dotnet.sh")

let cloneStage =
    stage "clone fsharp" {
        run (fun ctx ->
            async {
                if Directory.Exists fsharpDir then
                    printfn "fsharp/ already exists, not cloning. Delete it to start from a fresh checkout."
                    return Ok()
                else
                    return! ctx.RunCommand "git clone https://github.com/dotnet/fsharp --depth 1 -b main fsharp"
            })
    }

let buildFcsStage =
    stage "build FSharp.Compiler.Service" {
        workingDir fsharpDir
        run $"{upstreamDotnet} build FSharp.Compiler.Service.slnx"
    }

let restoreStage =
    stage "restore" {
        run "dotnet tool restore"
        run "dotnet restore FSharp.Compiler.Service/FSharp.Compiler.Service.fsproj"
    }

/// fsdocs targets a fixed .NET runtime. Let it run on whatever newer runtime the machine has,
/// prereleases included, so the SDK dotnet/fsharp needs is also enough to run the tool.
let fsdocsEnv =
    [ "DOTNET_ROLL_FORWARD", "LatestMajor"; "DOTNET_ROLL_FORWARD_TO_PRERELEASE", "1" ]

let fsdocsArgs = "--eval --sourcefolder fsharp --input fsharp/docs"

pipeline "Build" {
    description "Clone dotnet/fsharp, build FSharp.Compiler.Service and generate the docs into output/."
    workingDir root
    cloneStage
    buildFcsStage
    restoreStage
    stage "docs" {
        envVars fsdocsEnv
        run $"dotnet fsdocs build {fsdocsArgs}"
    }
    runIfOnlySpecified false
}

/// Everything after the pipeline name is passed on to `fsdocs watch`, for example
/// `dotnet fsi build.fsx -- -p Watch --nolaunch --port 8080`.
pipeline "Watch" {
    description "Serve the docs with live reload. Expects a Build to have run first."
    workingDir root
    restoreStage
    stage "watch" {
        envVars fsdocsEnv
        run (fun ctx ->
            let extraArgs =
                fsi.CommandLineArgs
                |> Array.skipWhile (fun arg -> arg <> "Watch")
                |> Array.skip 1
                |> String.concat " "

            ctx.RunCommand $"dotnet fsdocs watch {fsdocsArgs} {extraArgs}")
    }
    runIfOnlySpecified true
}

tryPrintPipelineCommandHelp ()
