namespace Ionide.VSCode.FSharp

open System
open Fable.Core
open Fable.Core.JsInterop
open Fable.Import
open Fable.Import.vscode
open Fable.Import.Node

open DTO
open Ionide.VSCode.Helpers

module MSBuild =
    let private outputChannel = window.createOutputChannel "msbuild"
    let private logger = ConsoleAndOutputChannelLogger(Some "msbuild", Level.DEBUG, Some outputChannel, Some Level.DEBUG)

    type MSbuildHost = 
        | MSBuildExe // .net on win, mono on non win
        | DotnetCli

    let mutable private msbuildHostType : MSbuildHost option = None

    let pickMSbuildHostType () =
        promise {
            let! envMsbuild = Environment.msbuild
            let! envDotnet = Environment.dotnet

            let hosts =
                [ sprintf ".NET (%s)" envMsbuild, MSbuildHost.MSBuildExe
                  sprintf ".NET Core (%s msbuild)" envDotnet, MSbuildHost.DotnetCli ]
                |> Map.ofList

            let hostsLabels = hosts |> Map.toList |> List.map fst |> ResizeArray
            let opts = createEmpty<QuickPickOptions>
            opts.placeHolder <- Some "The msbuild host to use"
            let! chosen = window.showQuickPick(hostsLabels |> Case1, opts)
            return
                if JS.isDefined chosen
                then
                    logger.Debug("user choose host %s", chosen)
                    hosts |> Map.tryFind chosen
                else
                    logger.Debug("user cancelled host pick")
                    None
        }


    let loadMSBuildHostCfg () = promise {
        let cfg = vscode.workspace.getConfiguration()
        return
            match cfg.get ("FSharp.msbuildHost", ".net") with
            | ".net" -> Some MSbuildHost.MSBuildExe
            | ".net core" -> Some MSbuildHost.DotnetCli
            | "ask at first use" -> None
            | _ -> Some MSbuildHost.MSBuildExe
        }

    let setMSbuildHostType host =
        msbuildHostType <- host
        match msbuildHostType with
        | Some MSbuildHost.MSBuildExe ->
            logger.Info("MSBuild (.NET) activated")
        | Some MSbuildHost.DotnetCli ->
            logger.Info("Dotnet cli (.NET Core) activated")
        | None ->
            logger.Info("Active msbuild: not choosen yet")

    let switchMSbuildHostType () = 
        promise {
            logger.Debug "switching msbuild host (msbuild <-> dotnet cli)"
            let! h =
                match msbuildHostType with
                | Some MSbuildHost.MSBuildExe ->
                    Some MSbuildHost.DotnetCli |> Promise.lift
                | Some MSbuildHost.DotnetCli ->
                    Some MSbuildHost.MSBuildExe |> Promise.lift
                | None ->
                    logger.Debug("not yet choosen, try pick one")
                    pickMSbuildHostType ()

            setMSbuildHostType h

            return h
        }

    let getMSbuildHostType () =
        match msbuildHostType with
        | Some h -> Some h |> Promise.lift
        | None ->
            promise {
                logger.Debug "No MSBuild host selected yet"
                let! hostFromConfig = loadMSBuildHostCfg ()
                let! ho =
                    match hostFromConfig with
                    | Some h -> Some h |> Promise.lift
                    | None -> pickMSbuildHostType ()

                ho |> Option.iter (Some >> setMSbuildHostType)

                return ho
            }

    let invokeMSBuild project target hostPreference =
        let autoshow =
            let cfg = vscode.workspace.getConfiguration()
            cfg.get ("FSharp.msbuildAutoshow", true)

        let safeproject = sprintf "\"%s\"" project
        let command = sprintf "%s /t:%s" safeproject target
        let executeWithHost host =
            promise {
                let! msbuildPath =
                    match host with
                    | MSbuildHost.MSBuildExe -> Environment.msbuild
                    | MSbuildHost.DotnetCli -> Environment.dotnet
                let cmd =
                    match host with
                    | MSbuildHost.MSBuildExe -> command
                    | MSbuildHost.DotnetCli -> sprintf "msbuild %s" command
                logger.Info("invoking msbuild from %s on %s for target %s", msbuildPath, safeproject, target)
                let _ =
                    if autoshow then outputChannel.show()
                return! Process.spawnWithNotification msbuildPath "" cmd outputChannel
                        |> Process.toPromise
            }

        let theMSbuildHostType =
            match hostPreference with
            | Some h -> Promise.lift (Some h)
            | None -> getMSbuildHostType ()
        
        theMSbuildHostType
        |> Promise.bind (function None -> Promise.empty | Some h -> executeWithHost h)

    /// discovers the project that the active document belongs to and builds that
    let buildCurrentProject target =
        logger.Debug("discovering project")
        match window.activeTextEditor.document with
        | Document.FSharp | Document.CSharp | Document.VB ->
            let currentProject = Project.getLoaded () |> Seq.where (fun p -> p.Files |> List.exists (String.endWith window.activeTextEditor.document.fileName)) |> Seq.tryHead
            match currentProject with
            | Some p ->
                logger.Debug("found project %s", p.Project)
                invokeMSBuild p.Project target None
            | None ->
                logger.Debug("could not find a project that contained the file %s", window.activeTextEditor.document.fileName)
                Promise.empty
        | Document.Other ->
                logger.Debug("I don't know how to handle a project of type %s", window.activeTextEditor.document.languageId)
                Promise.empty

    /// prompts the user to choose a project
    let pickProject placeHolder =
        logger.Debug "pick project"
        let projects = Project.getAll () |> ResizeArray
        if projects.Count = 0 then
            None |> Promise.lift
        else
            promise {
                let opts = createEmpty<QuickPickOptions>
                opts.placeHolder <- Some placeHolder
                let! chosen = window.showQuickPick(projects |> Case1, opts)
                logger.Debug("user chose project %s", chosen)
                return if JS.isDefined chosen
                       then Some chosen
                       else None
            }

    /// prompts the user to choose a project (if not specified) and builds that project
    let buildProject target projOpt hostOpt =
        promise {
            logger.Debug "building project"
            let! chosen =
                match projOpt with
                | None -> pickProject "Project to build"
                | Some h -> Some h |> Promise.lift
            return! match chosen with
                    | None -> Promise.empty
                    | Some proj -> invokeMSBuild proj target hostOpt
        }

    let activate disposables =
        let registerCommand com (action : unit -> _) = vscode.commands.registerCommand(com, unbox<Func<obj, obj>> action) |> ignore
        let registerCommand2 com (action : obj -> obj -> _) = vscode.commands.registerCommand(com, Func<obj, obj, obj>(fun a b -> action a b |> unbox)) |> ignore
        let onDidChangeConfiguration (action : unit -> _) = vscode.workspace.onDidChangeConfiguration.Invoke(unbox<Func<_, obj>>(fun _ -> action ())) |> ignore

        /// typed msbuild cmd. Optional project and msbuild host
        let typedMsbuildCmd f projOpt hostOpt =
            let p = if JS.isDefined projOpt then Some (unbox<string>(projOpt)) else None
            let h =
                match (if JS.isDefined hostOpt then unbox<int>(hostOpt) else 0) with
                | 1 -> Some MSbuildHost.MSBuildExe
                | 2 -> Some MSbuildHost.DotnetCli
                | 0 | _ -> None
            f p h

        let envMsbuild =
            Environment.msbuild
            |> Promise.map (fun p ->
                logger.Info("MSBuild (.NET) found at %s", p)
                p)

        let envDotnet = 
            Environment.dotnet
            |> Promise.map (fun p ->
                logger.Info("Dotnet cli (.NET Core) found at %s", p)
                p)

        let reloadCfg = loadMSBuildHostCfg >> Promise.map setMSbuildHostType

        [envMsbuild; envDotnet]
        |> Promise.all
        |> Promise.map Seq.toList
        |> Promise.bind (fun [_msbuild; _dotnet] -> reloadCfg ())
        |> ignore

        onDidChangeConfiguration(reloadCfg)

        registerCommand "MSBuild.buildCurrent" (fun _ -> buildCurrentProject "Build")
        registerCommand "MSBuild.rebuildCurrent" (fun _ -> buildCurrentProject "Rebuild")
        registerCommand "MSBuild.cleanCurrent" (fun _ -> buildCurrentProject "Clean")

        registerCommand2 "MSBuild.buildSelected" (typedMsbuildCmd (buildProject "Build"))
        registerCommand2 "MSBuild.rebuildSelected" (typedMsbuildCmd (buildProject "Rebuild"))
        registerCommand2 "MSBuild.cleanSelected" (typedMsbuildCmd (buildProject "Clean"))
        registerCommand2 "MSBuild.restoreSelected" (typedMsbuildCmd (buildProject"Restore"))

        registerCommand "MSBuild.switchMSbuildHostType" (fun _ -> switchMSbuildHostType ())
