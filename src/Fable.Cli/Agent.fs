module Fable.Cli.Agent

open Fable
open Fable.AST
open Fable.Transforms
open Fable.Transforms.State
open System
open System.Collections.Generic
open Microsoft.FSharp.Compiler.SourceCodeServices
open Newtonsoft.Json
open ProjectCracker

/// File.ReadAllText fails with locked files. See https://stackoverflow.com/a/1389172
let readAllText path =
    use fileStream = new IO.FileStream(path, IO.FileMode.Open, IO.FileAccess.Read, IO.FileShare.ReadWrite)
    use textReader = new IO.StreamReader(fileStream)
    textReader.ReadToEnd()

// let private dllCache = System.Collections.Concurrent.ConcurrentDictionary<string, byte[]>()

// // The InteractiveChecker expects only the file name without the .dll extension
// let makeReadAllBytes (dllPaths: string[]) =
//     // TODO: Temporary fix, what to do if two assemblies have the same name?
//     let dllNamesDic =
//         dllPaths |> Array.map (fun dllPath ->
//             IO.Path.GetFileNameWithoutExtension(dllPath), dllPath) |> dict
//     dllNamesDic.Keys |> Array.ofSeq,
//     fun (path: string) ->
//         // The InteractiveChecker adds the .dll extenstion at this point
//         let path = dllNamesDic.[path.[..(path.Length-5)]]
//         dllCache.GetOrAdd(path, fun _ -> IO.File.ReadAllBytes path)

let getSourceFiles (opts: FSharpProjectOptions) =
    opts.OtherOptions |> Array.choose (fun path ->
        // TODO: Dotnet.ProjInfo seems to not resolve well paths for .fsi files. Report?
        // For now, ignore them, though ncave said they're sometimes necessary for compilation
        if not(path.StartsWith("-")) && path.EndsWith(".fs") then
            // These should be already normalized, but just in case
            // TODO: We should add a NormalizedFullPath type so we don't need normalize everywhere
            Path.normalizeFullPath path |> Some
        else None)

let getRelativePath path =
    Path.getRelativePath (IO.Directory.GetCurrentDirectory()) path

let hasFlag flagName (opts: IDictionary<string, string>) =
    match opts.TryGetValue(flagName) with
    | true, value ->
        match bool.TryParse(value) with
        | true, value -> value
        | _ -> false
    | _ -> false

let tryGetOption name (opts: IDictionary<string, string>) =
    match opts.TryGetValue(name) with
    | true, value -> Some value
    | false, _ -> None

let splitVersion (version: string) =
    match System.Version.TryParse(version) with
    | true, v -> v.Major, v.Minor, v.Revision
    | _ -> 0, 0, 0

let checkFableCoreVersion (checkedProject: FSharpCheckProjectResults) =
    for ref in checkedProject.ProjectContext.GetReferencedAssemblies() do
        if ref.SimpleName = "Fable.Core" then
            let version = System.Text.RegularExpressions.Regex.Match(ref.QualifiedName, @"Version=(\d+\.\d+\.\d+)")
            let expectedMajor, expectedMinor, _ = splitVersion Literals.CORE_VERSION
            let actualMajor, actualMinor, _ = splitVersion version.Groups.[1].Value
            if not(actualMajor = expectedMajor && actualMinor = expectedMinor) then
                failwithf "Fable.Core v%i.%i detected, expecting v%i.%i" actualMajor actualMinor expectedMajor expectedMinor
            // else printfn "Fable.Core version matches"

let checkProject (msg: Parser.Message)
                 (opts: FSharpProjectOptions)
                 (fableLibraryDir: string)
                 (triggerFile: string)
                 (srcFiles: File[])
                 (checker: InteractiveChecker) =
    Log.logAlways(sprintf "Parsing %s..." (getRelativePath opts.ProjectFileName))
    let checkedProject =
        let filePaths, fileContents =
            srcFiles |> Array.map (fun file -> (file.NormalizedFullPath, file.Content)) |> Array.unzip
        checker.ParseAndCheckProject(opts.ProjectFileName, filePaths, fileContents)
    // checkFableCoreVersion checkedProject
    let optimized = GlobalParams.Singleton.Experimental.Contains("optimize-fcs")
    let implFiles =
        if not optimized
        then checkedProject.AssemblyContents.ImplementationFiles
        else checkedProject.GetOptimizedAssemblyContents().ImplementationFiles
    let implFilesMap =
        implFiles |> Seq.map (fun file -> (file.FileName, file)) |> Map
    tryGetOption "saveAst" msg.extra |> Option.iter (fun outDir ->
        Printers.printAst outDir implFiles)
    Project(opts, srcFiles, triggerFile, checker, implFilesMap, checkedProject.Errors, fableLibraryDir)

let createProject (msg: Parser.Message) projFile (prevProject: Project option) =
    match prevProject with
    | Some proj ->
        let mutable someDirtyFiles = false
        // If now - proj.TimeStamp < 1s skip checking the lastwritetime for performance
        if DateTime.Now - proj.TimeStamp < TimeSpan.FromSeconds(1.) then
            proj
        else
            let sourceFiles =
                proj.SourceFiles
                |> Array.map (fun file ->
                    let path = file.NormalizedFullPath
                    // Assume files in .fable folder are stable
                    if path.Contains(".fable/") then file
                    else
                        let isDirty = IO.File.GetLastWriteTime(path) > proj.TimeStamp
                        someDirtyFiles <- someDirtyFiles || isDirty
                        if isDirty then File(path, readAllText path)
                        else file)
            if someDirtyFiles then
                checkProject msg proj.ProjectOptions proj.LibraryDir msg.path sourceFiles proj.Checker
            else proj
    | None ->
        let projectOptions, fableLibraryDir =
            getFullProjectOpts msg.define msg.rootDir projFile
        Log.logVerbose(lazy
            let proj = getRelativePath projectOptions.ProjectFileName
            let opts = projectOptions.OtherOptions |> String.concat "\n   "
            sprintf "F# PROJECT: %s\n   %s" proj opts)
        let sourceFiles =
            getSourceFiles projectOptions
            |> Array.map (fun path -> File(path, readAllText path))
        InteractiveChecker.Create(projectOptions)
        |> checkProject msg projectOptions fableLibraryDir projFile sourceFiles

let jsonSettings =
    JsonSerializerSettings(
        Converters=[|Json.ErasedUnionConverter()|],
        ContractResolver=Serialization.CamelCasePropertyNamesContractResolver(),
        NullValueHandling=NullValueHandling.Ignore)
        // StringEscapeHandling=StringEscapeHandling.EscapeNonAscii)

let sendError (respond: obj->unit) (ex: Exception) =
    let rec innerStack (ex: Exception) =
        if isNull ex.InnerException then ex.StackTrace else innerStack ex.InnerException
    let stack = innerStack ex
    Log.logAlways(sprintf "ERROR: %s\n%s" ex.Message stack)
    ["error", ex.Message] |> dict |> respond

let rec findFsprojUpwards originalFile dir =
    match IO.Directory.GetFiles(dir, "*.fsproj") with
    | [||] ->
        let parentDir = IO.Path.GetDirectoryName(dir)
        if isNull parentDir
        then failwithf "Cannot find project file for %s. Do you need symlinks:false in your webpack.config?" originalFile
        else findFsprojUpwards originalFile parentDir
    | [|projFile|] -> projFile
    | _ -> failwithf "Found more than one project file for %s, please disambiguate." originalFile

let addOrUpdateProject state (project: Project) =
    let state = Map.add project.ProjectFile project state
    state, project

let tryFindAndUpdateProject state (msg: Parser.Message) sourceFile =
    let checkIfProjectIsAlreadyInState projFile =
        let projFile = Path.normalizeFullPath projFile
        Map.tryFind projFile state
        |> createProject msg projFile
        |> addOrUpdateProject state

    // Check for the `extra.projectFile` option. This is used to
    // disambiguate files referenced by several projects, see #1116
    match msg.extra.TryGetValue("projectFile") with
    | true, projFile ->
        let projFile = Path.normalizeFullPath projFile
        checkIfProjectIsAlreadyInState projFile
    | false, _ ->
        state |> Map.tryPick (fun _ (project: Project) ->
            if project.ContainsFile(sourceFile)
            then Some project
            else None)
        |> function
            | Some project ->
                Some project
                |> createProject msg project.ProjectFile
                |> addOrUpdateProject state
            | None ->
                IO.Path.GetDirectoryName(sourceFile)
                |> findFsprojUpwards sourceFile
                |> checkIfProjectIsAlreadyInState

let updateState (state: Map<string,Project>) (msg: Parser.Message) =
    match IO.Path.GetExtension(msg.path).ToLower() with
    | ".fsproj" ->
        createProject msg msg.path None
        |> addOrUpdateProject state
    | ".fsx" ->
        // When a script is modified, restart the project with new options
        // (to check for new references, loaded projects, etc.)
        createProject msg msg.path None
        |> addOrUpdateProject state
    | ".fs" ->
        tryFindAndUpdateProject state msg msg.path
    | ".fsi" ->
        failwithf "Signature files cannot be compiled to JS: %s" msg.path
    | _ -> failwithf "Not an F# source file: %s" msg.path

let addFSharpErrorLogs (com: ICompiler) (proj: Project) =
    proj.Errors |> Seq.filter (fun er ->
        let skip =
            // If the trigger file is the .fsproj reports errors in the corresponding file.
            // If another file triggers the compilation (as in watch mode) reports errors there so they don't go missing
            if proj.TriggerFile.EndsWith(".fsproj") then
                er.FileName <> com.CurrentFile
            else proj.TriggerFile <> com.CurrentFile
        match skip, er.Severity with
        | true, _ -> false
        | false, FSharpErrorSeverity.Error -> true
        | false, FSharpErrorSeverity.Warning ->
            // TODO: Check level and disabled warnings from project options
            // From https://github.com/Microsoft/visualfsharp/blob/1bbb60a4ee1bfc1dd1a070d043f5fb012bf74bc4/src/fsharp/CompileOps.fs#L241-L393
            match er.ErrorNumber with
            // Level 5 warnings
            | 21 // RecursiveUseCheckedAtRuntime
            | 22 // LetRecEvaluatedOutOfOrder
            | 45 // FullAbstraction
            | 52 // DefensiveCopyWarning
            | 1178 // tcNoComparisonNeeded/tcNoEqualityNeeded1

            // Warnings off by default
            | 1182 // chkUnusedValue - off by default
            | 3218 // ArgumentsInSigAndImplMismatch - off by default
            | 3180 // abImplicitHeapAllocation - off by default
                -> false
            // Level 2 warnings
            | _ -> true)
    |> Seq.map (fun er ->
        let severity =
            match er.Severity with
            | FSharpErrorSeverity.Warning -> Severity.Warning
            | FSharpErrorSeverity.Error -> Severity.Error
        let range =
            { start={ line=er.StartLineAlternate; column=er.StartColumn}
              ``end``={ line=er.EndLineAlternate; column=er.EndColumn}
              identifierName = None }
        (er.FileName, range, severity, sprintf "%s (code %i)" er.Message er.ErrorNumber))
    |> Seq.distinct // Sometimes errors are duplicated
    |> Seq.iter (fun (fileName, range, severity, msg) ->
        com.AddLog(msg, severity, range, fileName, "FSHARP"))

/// Don't await file compilation to let the agent receive more requests to implement files.
let startCompilation (respond: obj->unit) (com: Compiler) (project: Project) =
    async {
        try
            if com.CurrentFile.EndsWith(".fsproj") then
                // If we compile the last file here, Webpack watcher will ignore changes in it
                Fable2Babel.Compiler.createFacade (getSourceFiles project.ProjectOptions) com.CurrentFile
                |> respond
            else
                let babel =
                    FSharp2Fable.Compiler.transformFile com project.ImplementationFiles
                    |> FableTransforms.optimizeFile com
                    |> Fable2Babel.Compiler.transformFile com
                Babel.Program(babel.FileName, babel.Body, babel.Directives, com.GetFormattedLogs(), babel.Dependencies)
                |> respond
        with ex ->
            sendError respond ex
    } |> Async.Start

let startAgent () = MailboxProcessor<AgentMsg>.Start(fun agent ->
    let rec loop (state: Map<string,Project>) = async {
      match! agent.Receive() with
      | Respond(value, msgHandler) ->
        msgHandler.Respond(fun writer ->
            // CloseOutput=false is necessary to prevent closing the underlying stream
            use jsonWriter = new JsonTextWriter(writer, CloseOutput=false)
            let serializer = JsonSerializer.Create(jsonSettings)
            serializer.Serialize(jsonWriter, value))
        return! loop state
      | Received msgHandler ->
        let respond(res: obj) =
            Respond(res, msgHandler) |> agent.Post
        try
            let msg = Parser.parse msgHandler.Message
            // lazy sprintf "Received message %A" msg |> Log.logVerbose
            let newState, activeProject = updateState state msg
            let com = Compiler(msg.path, activeProject, Parser.toCompilerOptions msg)
            addFSharpErrorLogs com activeProject
            startCompilation respond com activeProject
            return! loop newState
        with ex ->
            sendError respond ex
            return! loop state
    }
    loop Map.empty
  )