#!/usr/bin/env -S dotnet fsi --
#r "nuget: Fun.Build, 1.1.18"

open System
open System.IO
open Fun.Build

let root = __SOURCE_DIRECTORY__

/// Set this to the path of an existing dotnet/fsharp checkout to generate the docs from it
/// instead of the shallow clone the pipeline makes. Handy when iterating on docs changes in
/// a local fork: the clone stage is skipped and the checkout is built and read in place.
let fsharpRepoEnvVar = "FSHARP_REPO"

/// The dotnet/fsharp checkout the docs are generated from: its `docs` folder and the
/// FSharp.Compiler.Service it builds. By default a git-ignored shallow clone of main.
let fsharpRepo = Environment.GetEnvironmentVariable fsharpRepoEnvVar

let fsharpDir =
    if String.IsNullOrEmpty fsharpRepo then
        Path.Combine(root, "fsharp")
    else
        Path.GetFullPath fsharpRepo

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
                if not (String.IsNullOrEmpty fsharpRepo) then
                    if Directory.Exists fsharpDir then
                        printfn $"Using the dotnet/fsharp checkout at {fsharpDir} ({fsharpRepoEnvVar})."
                        return Ok()
                    else
                        return Error $"{fsharpRepoEnvVar} points to {fsharpDir}, which does not exist."
                elif Directory.Exists fsharpDir then
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
/// FSHARP_REPO is passed on so FSharp.Compiler.Service.fsproj resolves the assembly from the
/// same checkout.
let fsdocsEnv =
    [ "DOTNET_ROLL_FORWARD", "LatestMajor"
      "DOTNET_ROLL_FORWARD_TO_PRERELEASE", "1"
      fsharpRepoEnvVar, fsharpDir ]

let fsdocsArgs =
    let docsDir = Path.Combine(fsharpDir, "docs")
    $"--eval --sourcefolder \"{fsharpDir}\" --input \"{docsDir}\""

pipeline "Build" {
    description "Clone dotnet/fsharp, build FSharp.Compiler.Service and generate the docs into output/."
    workingDir root
    cloneStage
    buildFcsStage
    restoreStage
    stage "docs" {
        envVars fsdocsEnv
        // fsdocs caches the cracked project, including the FSharp.Compiler.Service.dll it resolved,
        // in .fsdocs/cache and does not notice when FSHARP_REPO changes. Drop it, and pass --clean
        // so pages from a previous build against another checkout never linger in output/.
        run (fun _ ->
            let cache = Path.Combine(root, ".fsdocs", "cache")
            // File.Delete tolerates a missing file but not a missing directory, as on a fresh clone.
            if File.Exists cache then
                File.Delete cache)
        run $"dotnet fsdocs build --clean {fsdocsArgs}"
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
