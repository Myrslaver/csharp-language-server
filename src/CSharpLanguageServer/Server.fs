module CSharpLanguageServer.Server

open System
open System.IO
open System.Collections.Generic
open System.Collections.Immutable

open Microsoft.CodeAnalysis
open Microsoft.CodeAnalysis.FindSymbols
open Microsoft.CodeAnalysis.Text
open Microsoft.CodeAnalysis.Completion
open Microsoft.CodeAnalysis.Rename
open FSharp.Control.Tasks.V2

open LanguageServerProtocol.Server
open LanguageServerProtocol
open LanguageServerProtocol.Types
open LanguageServerProtocol.LspResult

open RoslynHelpers
open Microsoft.CodeAnalysis.CodeRefactorings
open System.Threading
open Microsoft.CodeAnalysis.CodeFixes
open ICSharpCode.Decompiler.Metadata
open ICSharpCode.Decompiler.CSharp
open System.Reflection.PortableExecutable
open ICSharpCode.Decompiler
open ICSharpCode.Decompiler.CSharp.Transforms

type CSharpLspClient(sendServerNotification: ClientNotificationSender, sendServerRequest: ClientRequestSender) =
    inherit LspClient ()

    override __.WindowShowMessage(p) =
        sendServerNotification "window/showMessage" (box p) |> Async.Ignore

    override __.WindowShowMessageRequest(p) =
        sendServerRequest.Send "window/showMessageRequest" (box p)

    override __.WindowLogMessage(p) =
        sendServerNotification "window/logMessage" (box p) |> Async.Ignore

    override __.TelemetryEvent(p) =
        sendServerNotification "telemetry/event" (box p) |> Async.Ignore

    override __.ClientRegisterCapability(p) =
        sendServerRequest.Send "client/registerCapability" (box p)

    override __.ClientUnregisterCapability(p) =
        sendServerRequest.Send "client/unregisterCapability" (box p)

    override __.WorkspaceWorkspaceFolders () =
        sendServerRequest.Send "workspace/workspaceFolders" ()

    override __.WorkspaceConfiguration (p) =
        sendServerRequest.Send "workspace/configuration" (box p)

    override __.WorkspaceApplyEdit (p) =
        sendServerRequest.Send "workspace/applyEdit" (box p)

    override __.WorkspaceSemanticTokensRefresh () =
      sendServerNotification "workspace/semanticTokens/refresh" () |> Async.Ignore

    override __.TextDocumentPublishDiagnostics(p) =
        sendServerNotification "textDocument/publishDiagnostics" (box p) |> Async.Ignore

type CSharpMetadataParams = {
    ProjectName: string
    AssemblyName: string
    TypeName: string
}

type CSharpMetadataResponse = {
    SourceName: string
    Source: string
}

type CSharpLspServer(lspClient: CSharpLspClient) =
    inherit LspServer()

    let mutable clientCapabilities: ClientCapabilities option = None
    let mutable currentSolution: Solution option = None
    let mutable openDocVersions = Map.empty<string, int>
    let mutable decompiledMetadataUris = Map.empty<string, string>
    let mutable decompiledMetadataDocs = Map.empty<string, Document>

    let logMessage message =
        let messageParams = { Type = MessageType.Log ; Message = "csharp-ls: " + message }
        do lspClient.WindowShowMessage messageParams |> ignore

    let mutable deferredInitialize = async {
        let cwd = Directory.GetCurrentDirectory()
        let! solutionLoaded = loadSolutionOnDir logMessage cwd
        currentSolution <- solutionLoaded
    }

    let getPathUri path = Uri("file://" + path)

    let getDocumentForUri u =
        let uri = Uri u
        match currentSolution with
        | Some solution -> let documents = solution.Projects |> Seq.collect (fun p -> p.Documents)
                           let matchingDocuments = documents |> Seq.filter (fun d -> uri = getPathUri d.FilePath) |> List.ofSeq
                           match matchingDocuments with
                           | [d] -> //logMessage ("resolved to document " + d.FilePath + " while looking for " + uri.ToString())
                                    Some d
                           | _ -> Map.tryFind u decompiledMetadataDocs
        | None -> None

    let getSymbolAtPosition documentUri pos = async {
        match getDocumentForUri documentUri with
        | Some doc ->
            let! sourceText = doc.GetTextAsync() |> Async.AwaitTask
            let position = sourceText.Lines.GetPosition(LinePosition(pos.Line, pos.Character))
            let! symbolRef = SymbolFinder.FindSymbolAtPositionAsync(doc, position) |> Async.AwaitTask
            return if isNull symbolRef then None else Some (symbolRef, doc)
        | None ->
            return None
    }

    let mapDiagSeverity s =
        match s with
        | Microsoft.CodeAnalysis.DiagnosticSeverity.Info -> Some LanguageServerProtocol.Types.DiagnosticSeverity.Information
        | Microsoft.CodeAnalysis.DiagnosticSeverity.Warning -> Some LanguageServerProtocol.Types.DiagnosticSeverity.Warning
        | Microsoft.CodeAnalysis.DiagnosticSeverity.Error -> Some LanguageServerProtocol.Types.DiagnosticSeverity.Error
        | Microsoft.CodeAnalysis.DiagnosticSeverity.Hidden -> None
        | _ -> None

    let makeLspDiag (d: Microsoft.CodeAnalysis.Diagnostic) =
        { Range = d.Location.GetLineSpan().Span |> lspRangeForRoslynLinePosSpan
          Severity = mapDiagSeverity d.Severity
          Code = None
          CodeDescription = None
          Source = "lsp"
          Message = d.GetMessage()
          RelatedInformation = None
          Tags = None
          Data = None }

    let locationToLspLocation (loc: Microsoft.CodeAnalysis.Location) =
        { Uri = loc.SourceTree.FilePath |> getPathUri |> string
          Range = loc.GetLineSpan().Span |> lspRangeForRoslynLinePosSpan }

    let getContainingTypeOrThis (symbol: ISymbol): INamedTypeSymbol =
        if (symbol :? INamedTypeSymbol) then
            symbol :?> INamedTypeSymbol
        else
            symbol.ContainingType

    let getFullReflectionName (containingType: INamedTypeSymbol) =
        let stack = Stack<string>();
        stack.Push(containingType.MetadataName);
        let mutable ns = containingType.ContainingNamespace;

        let mutable doContinue = true
        while doContinue do
            stack.Push(ns.Name);
            ns <- ns.ContainingNamespace

            doContinue <- ns <> null && not ns.IsGlobalNamespace

        String.Join(".", stack)


    override _.Initialize(p: InitializeParams) = async {
        clientCapabilities <- p.Capabilities

        return
            { InitializeResult.Default with
                Capabilities =
                    { ServerCapabilities.Default with
                        HoverProvider = Some true
                        RenameProvider = Some true
                        DefinitionProvider = Some true
                        TypeDefinitionProvider = Some false
                        ImplementationProvider = Some false
                        ReferencesProvider = Some true
                        DocumentHighlightProvider = Some true
                        DocumentSymbolProvider = Some true
                        WorkspaceSymbolProvider = Some true
                        DocumentFormattingProvider = Some false
                        DocumentRangeFormattingProvider = Some false
                        SignatureHelpProvider = None
                        CompletionProvider =
                            Some { ResolveProvider = None
                                   TriggerCharacters = Some ([| '.'; '''; |])
                                   AllCommitCharacters = None
                                 }
                        CodeLensProvider = None
                        CodeActionProvider = Some true
                        TextDocumentSync =
                            Some { TextDocumentSyncOptions.Default with
                                     OpenClose = Some false
                                     Save = Some { IncludeText = Some true }
                                     Change = Some TextDocumentSyncKind.Full
                                 }
                        FoldingRangeProvider = Some false
                        SelectionRangeProvider = Some false
                        SemanticTokensProvider = None
                    }
            }
        |> success
    }

    override __.Initialized(_: InitializedParams) = async {
        do! deferredInitialize
        return ()
    }

    override __.Exit(): Async<unit> = async { return () }

    override __.Shutdown(): Async<unit> = async { return () }

    override __.TextDocumentDidOpen(openParams: Types.DidOpenTextDocumentParams) = async {
        let document = getDocumentForUri openParams.TextDocument.Uri

        openDocVersions <- openDocVersions.Add(openParams.TextDocument.Uri, openParams.TextDocument.Version)

        match document with
        | Some doc ->
            let! semanticModel = doc.GetSemanticModelAsync() |> Async.AwaitTask

            let diagnostics = semanticModel.GetDiagnostics()
                              |> Seq.map makeLspDiag
                              |> Array.ofSeq

            do! lspClient.TextDocumentPublishDiagnostics {
                Uri = openParams.TextDocument.Uri ;
                Diagnostics = diagnostics }

            return ()
        | None -> ()
    }

    override __.TextDocumentDidChange(changeParams: Types.DidChangeTextDocumentParams): Async<unit> = async {
        openDocVersions <- openDocVersions.Add(
            changeParams.TextDocument.Uri,
            changeParams.TextDocument.Version |> Option.defaultValue 0)

        match getDocumentForUri changeParams.TextDocument.Uri with
        | Some doc ->
            let fullText = SourceText.From(changeParams.ContentChanges.[0].Text)

            let updatedDoc = doc.WithText(fullText)
            let updatedSolution = updatedDoc.Project.Solution;

            currentSolution <- Some updatedSolution

            let! semanticModel = updatedDoc.GetSemanticModelAsync() |> Async.AwaitTask

            let diagnostics = semanticModel.GetDiagnostics()
                                           |> Seq.map makeLspDiag
                                           |> Array.ofSeq

            do! lspClient.TextDocumentPublishDiagnostics {
                  Uri = changeParams.TextDocument.Uri
                  Diagnostics = diagnostics }
            return ()

        | _ -> return ()
    }

    override __.TextDocumentDidSave(_: Types.DidSaveTextDocumentParams): Async<unit> = async {
        ()
    }

    override __.TextDocumentDidClose(closeParams: Types.DidCloseTextDocumentParams): Async<unit> = async {
        openDocVersions <- openDocVersions.Remove(closeParams.TextDocument.Uri)
        return ()
    }

    override __.TextDocumentCodeAction(actionParams: Types.CodeActionParams): AsyncLspResult<Types.TextDocumentCodeActionResult option> = async {

        match getDocumentForUri actionParams.TextDocument.Uri with
        | None ->
            return None |> success

        | Some doc ->
            let! docText = doc.GetTextAsync() |> Async.AwaitTask

            let textSpan = actionParams.Range |> roslynLinePositionSpanForLspRange
                                              |> docText.Lines.GetTextSpan

            // register code actions
            let roslynCodeActions = List<CodeActions.CodeAction>()
            let addCodeAction = Action<CodeActions.CodeAction>(roslynCodeActions.Add)
            let codeActionContext = CodeRefactoringContext(doc, textSpan, addCodeAction, CancellationToken.None)

            for refactoringProvider in refactoringProviderInstances do
                do! refactoringProvider.ComputeRefactoringsAsync(codeActionContext) |> Async.AwaitTask

            // register code fixes
            let! semanticModel = doc.GetSemanticModelAsync() |> Async.AwaitTask

            let isDiagnosticsOnTextSpan (diag: Microsoft.CodeAnalysis.Diagnostic) =
                diag.Location.SourceSpan.IntersectsWith(textSpan)

            let relatedDiagnostics = semanticModel.GetDiagnostics() |> Seq.filter isDiagnosticsOnTextSpan |> List.ofSeq
            //logMessage (sprintf "relatedDiagnostics.Count=%d" (relatedDiagnostics.Count()))

            if relatedDiagnostics.Length > 0 then
                let addCodeFix =
                    Action<CodeActions.CodeAction, ImmutableArray<Microsoft.CodeAnalysis.Diagnostic>>(
                        fun ca _ -> roslynCodeActions.Add(ca))

                for refactoringProvider in codeFixProviderInstances do
                    //logMessage (sprintf "%s: .FixableDiagnosticIds=%s" (refactoringProvider |> string)
                    //                                                   (JsonConvert.SerializeObject(refactoringProvider.FixableDiagnosticIds)))

                    for diag in relatedDiagnostics do
                        let codeFixContext = CodeFixContext(doc, diag, addCodeFix, CancellationToken.None)

                        logMessage (sprintf "%s: Id=%s" (diag |> string) diag.Id)

                        let refactoringProviderOK =
                            let translatedDiagId diagId =
                                match diagId with
                                | "CS8019" -> "RemoveUnnecessaryImportsFixable"
                                | _ -> ""

                            refactoringProvider.FixableDiagnosticIds.Contains(diag.Id)
                            || refactoringProvider.FixableDiagnosticIds.Contains(translatedDiagId diag.Id)

                        try
                            if refactoringProviderOK then
                                do! refactoringProvider.RegisterCodeFixesAsync(codeFixContext) |> Async.AwaitTask
                        with _ex ->
                            //sprintf "error in RegisterCodeFixesAsync(): %s" (ex.ToString()) |> logMessage
                            ()

            let! lspCodeActions = roslynCodeActions
                                  |> Seq.map (roslynCodeActionToLspCodeAction currentSolution.Value
                                                                              openDocVersions.TryFind
                                                                              logMessage)
                                  |> Async.Sequential

            return lspCodeActions |> Seq.collect (fun maybeAction -> match maybeAction with
                                                                     | Some action -> [action]
                                                                     | None -> [])
                                  |> Array.ofSeq
                                  |> TextDocumentCodeActionResult.CodeActions
                                  |> Some
                                  |> success
    }

    override __.TextDocumentCompletion(posParams: Types.CompletionParams): AsyncLspResult<Types.CompletionList option> = async {
        match getDocumentForUri posParams.TextDocument.Uri with
        | Some doc ->
            let! docText = doc.GetTextAsync() |> Async.AwaitTask
            let posInText = docText.Lines.GetPosition(LinePosition(posParams.Position.Line, posParams.Position.Character))

            let completionService = CompletionService.GetService(doc)
            if isNull completionService then
                return ()

            let! maybeCompletionResults = completionService.GetCompletionsAsync(doc, posInText)
                                          |> Async.AwaitTask

            match Option.ofObj maybeCompletionResults with
            | Some completionResults ->
                let makeLspCompletionItem (item: Microsoft.CodeAnalysis.Completion.CompletionItem) =
                    { CompletionItem.Create(item.DisplayText) with
                        Kind = item.Tags |> Seq.head |> roslynTagToLspCompletion |> Some ;
                        InsertTextFormat = Some Types.InsertTextFormat.PlainText }

                let completionList = {
                    IsIncomplete = false ;
                    Items = completionResults.Items
                                              |> Seq.map makeLspCompletionItem
                                              |> Array.ofSeq }

                return success (Some completionList)
            | None -> return success None
        | None -> return success None
    }

    override __.TextDocumentDefinition(def: Types.TextDocumentPositionParams): AsyncLspResult<Types.GotoResult option> = async {
        logMessage (sprintf "TextDocumentDefinition: uri %s; pos %s" (def.TextDocument.Uri |> string) (def.Position |> string))

        let resolveDefinition (doc: Document) = task {
            logMessage (sprintf "TextDocumentDefinition/resolveDefinition: resolving symbol @ %s in %s" (def.Position |> string) (doc |> string))

            let! sourceText = doc.GetTextAsync()
            let position = sourceText.Lines.GetPosition(LinePosition(def.Position.Line, def.Position.Character))
            let! symbol = SymbolFinder.FindSymbolAtPositionAsync(doc, position)
            let! compilation = doc.Project.GetCompilationAsync()

            let! locations = task {
                match symbol with
                | null -> //logMessage "no symbol at this point"
                    return []

                | sym ->
                    //logMessage ("have symbol " + sym.ToString() + " at this point!")
                    // TODO:
                    //  - handle symbols in metadata

                    let locationsInSource = sym.Locations |> Seq.filter (fun l -> l.IsInSource)

                    let locationsInMetadata = sym.Locations |> Seq.filter (fun l -> l.IsInMetadata)

                    let haveLocationsInMetadata = locationsInMetadata |> Seq.isEmpty |> not

                    if haveLocationsInMetadata then
                        do logMessage (sprintf "have locations in metadata: %s" (String.Join(", ", locationsInMetadata |> Seq.map string)))

                        let mdLocation = Seq.head locationsInMetadata

                        let reference = compilation.GetMetadataReference(mdLocation.MetadataModule.ContainingAssembly)
                        let peReference = reference :?> PortableExecutableReference |> Option.ofObj
                        let assemblyLocation = peReference |> Option.map (fun r -> r.FilePath) |> Option.defaultValue "???"

                        let decompiler = CSharpDecompiler(assemblyLocation, DecompilerSettings())

                        // Escape invalid identifiers to prevent Roslyn from failing to parse the generated code.
                        // (This happens for example, when there is compiler-generated code that is not yet recognized/transformed by the decompiler.)
                        decompiler.AstTransforms.Add(EscapeInvalidIdentifiers())

                        let fullName = sym |> getContainingTypeOrThis |> getFullReflectionName
                        let fullTypeName = ICSharpCode.Decompiler.TypeSystem.FullTypeName(fullName)

                        let text = decompiler.DecompileTypeAsString(fullTypeName)

                        let uri = $"csharp:/metadata/projects/{doc.Project.Name}/assemblies/{mdLocation.MetadataModule.ContainingAssembly.Name}/symbols/{fullName}.cs"

                        decompiledMetadataUris <- decompiledMetadataUris.Add(uri, text)

                        let mdDocumentFilename = $"$metadata$/projects/{doc.Project.Name}/assemblies/{mdLocation.MetadataModule.ContainingAssembly.Name}/symbols/{fullName}.cs"
                        let mdProject = doc.Project
                        let mdDocumentEmpty = mdProject.AddDocument(mdDocumentFilename, String.Empty)
                        let mdDocument = SourceText.From(text) |> mdDocumentEmpty.WithText

                        decompiledMetadataDocs <- decompiledMetadataDocs |> Map.add uri mdDocument

                        logMessage (sprintf "have mdDocument added to project, filename %s" mdDocumentFilename)

                        // figure out location on the document (approx implementation)
                        logMessage (sprintf "sym.Name = %s" sym.Name)

                        let! syntaxTree = mdDocument.GetSyntaxTreeAsync()
                        let collector = DocumentSymbolCollectorForMatchingSymbolName(uri, sym.Name)
                        collector.Visit(syntaxTree.GetRoot())

                        let fallbackLocationInMetadata = {
                            Uri = uri
                            Range = { Start = { Line = 0; Character = 0 }; End = { Line = 0; Character = 1 } } }

                        let locationInMetadata =
                            (collector.GetLocations() @ [ fallbackLocationInMetadata ])
                            |> Seq.head

                        logMessage (sprintf "returning uri=%s; range=%s"
                                            locationInMetadata.Uri
                                            (locationInMetadata.Range |> string))

                        return [locationInMetadata]
                    else
                        match List.ofSeq locationsInSource with
                        | [] -> return []
                        | locations -> return locations |> Seq.map locationToLspLocation |> List.ofSeq
                }

            return locations
        }

        match getDocumentForUri def.TextDocument.Uri with
        | Some doc -> let! locations = resolveDefinition doc |> Async.AwaitTask
                      return locations |> Array.ofSeq
                                       |> GotoResult.Multiple
                                       |> Some
                                       |> success
        | None     ->
            logMessage (sprintf "TextDocumentDefinition: no document for uri %s" (def.TextDocument.Uri |> string))
            return None |> success
    }

    override __.TextDocumentDocumentHighlight(docParams: Types.TextDocumentPositionParams): AsyncLspResult<Types.DocumentHighlight [] option> = async {
        match currentSolution with
        | Some solution ->
            let! maybeSymbol = getSymbolAtPosition docParams.TextDocument.Uri docParams.Position

            match maybeSymbol with
            | Some (symbol, doc) ->
                let docSet = ImmutableHashSet<Document>.Empty.Add(doc)
                let! refs = SymbolFinder.FindReferencesAsync(symbol, solution, docSet) |> Async.AwaitTask
                let locationsFromRefs = refs |> Seq.collect (fun r -> r.Locations) |> Seq.map (fun rl -> rl.Location)

                let! defRef = SymbolFinder.FindSourceDefinitionAsync(symbol, solution) |> Async.AwaitTask
                let locationsFromDef = match Option.ofObj defRef with
                                       // TODO: we might need to skip locations that are on a different document than this one
                                       | Some sym -> sym.Locations |> List.ofSeq
                                       | None -> []

                return (Seq.append locationsFromRefs locationsFromDef)
                       |> Seq.map (fun l -> { Range = (locationToLspLocation l).Range ;
                                              Kind = Some DocumentHighlightKind.Read })
                       |> Array.ofSeq
                       |> Some
                       |> success

            | None -> return None |> success

        | None -> return None |> success
    }

    override __.TextDocumentDocumentSymbol(p: Types.DocumentSymbolParams): AsyncLspResult<Types.SymbolInformation [] option> = async {
        match getDocumentForUri p.TextDocument.Uri with
        | Some doc ->
            let collector = DocumentSymbolCollector(p.TextDocument.Uri)

            let! syntaxTree = doc.GetSyntaxTreeAsync() |> Async.AwaitTask
            collector.Visit(syntaxTree.GetRoot())

            return collector.GetSymbols() |> Some |> success

        | None ->
            return None |> success
    }

    override __.TextDocumentHover(hoverPos: Types.TextDocumentPositionParams): AsyncLspResult<Types.Hover option> = async {
        let! maybeSymbol = getSymbolAtPosition hoverPos.TextDocument.Uri hoverPos.Position
        let maybeHoverText = Option.map (fun (sym: ISymbol, _: Document) -> sym.ToString() + "\n" +  sym.GetDocumentationCommentXml()) maybeSymbol

        let contents = [ match maybeHoverText with
                         | None -> MarkedString.WithLanguage { Language = "csharp"; Value = "" }
                         | Some text -> MarkedString.WithLanguage { Language = "csharp"; Value = text }
                       ]

        return Some { Contents = contents |> Array.ofList |> MarkedStrings
                      Range = None }
               |> success
    }

    override __.TextDocumentReferences(refParams: Types.ReferenceParams): AsyncLspResult<Types.Location [] option> = async {
        match currentSolution with
        | Some solution ->
            let! maybeSymbol = getSymbolAtPosition refParams.TextDocument.Uri refParams.Position

            match maybeSymbol with
            | Some (symbol, _) ->
                let! refs = SymbolFinder.FindReferencesAsync(symbol, solution) |> Async.AwaitTask
                return refs |> Seq.collect (fun r -> r.Locations)
                            |> Seq.map (fun rl -> locationToLspLocation rl.Location)
                            |> Array.ofSeq
                            |> Some
                            |> success
            | None -> return None |> success

        | None -> return None |> success
    }

    override __.TextDocumentRename(rename: Types.RenameParams): AsyncLspResult<Types.WorkspaceEdit option> = async {
        let renameSymbolInDoc symbol (doc: Document) = async {
            let originalSolution = doc.Project.Solution
            let! updatedSolution = Renamer.RenameSymbolAsync(doc.Project.Solution,
                                                             symbol,
                                                             rename.NewName,
                                                             doc.Project.Solution.Workspace.Options)
                                   |> Async.AwaitTask

            let! docTextEdit = lspDocChangesFromSolutionDiff originalSolution
                                                             updatedSolution
                                                             openDocVersions.TryFind
            return docTextEdit
        }

        let! maybeSymbol = getSymbolAtPosition rename.TextDocument.Uri rename.Position

        let! docChanges = match maybeSymbol with
                            | Some (symbol, doc) -> renameSymbolInDoc symbol doc
                            | None -> async { return [] }

        return WorkspaceEdit.Create (docChanges |> Array.ofList, clientCapabilities.Value) |> Some |> success
    }

    override __.WorkspaceSymbol(symbolParams: Types.WorkspaceSymbolParams): AsyncLspResult<Types.SymbolInformation [] option> = async {
        let! symbols = findSymbols currentSolution.Value symbolParams.Query (Some 20)
        return symbols |> Array.ofSeq |> Some |> success
    }

    member __.CSharpMetadata (metadataParams: CSharpMetadataParams): AsyncLspResult<CSharpMetadataResponse option> = async {
        let uri = $"csharp:/metadata/projects/{metadataParams.ProjectName}/assemblies/{metadataParams.AssemblyName}/symbols/{metadataParams.TypeName}.cs"

        logMessage (sprintf "CSharpMetadata: attempting to get source for uri %s" uri)

        let maybeSource = decompiledMetadataUris |> Map.tryFind uri

        //logMessage (sprintf "CSharpMetadata: got %s" (maybeSource |> Option.defaultValue "(none)"))

        let normalizeComponent (c: string) = c.Replace(".", "/")

        let sourceName = sprintf "$metadata$/projects/%s/assemblies/%s/symbols/%s.cs"
                                 (metadataParams.ProjectName |> normalizeComponent)
                                 (metadataParams.AssemblyName |> normalizeComponent)
                                 (metadataParams.TypeName |> normalizeComponent)

        let yesNo opt = match opt with
                        | Some _ -> "yes"
                        | None -> "no"

        logMessage (sprintf "sourceName: %s; have source: %s" sourceName (maybeSource |> yesNo))

        return maybeSource
               |> Option.map (fun source -> { SourceName = sourceName; Source = source })
               |> success
    }

    override __.Dispose(): unit = ()


let startCore () =
    use input = Console.OpenStandardInput()
    use output = Console.OpenStandardOutput()

    let requestHandlings =
        defaultRequestHandlings<CSharpLspServer> ()
        |> Map.add "csharp/metadata" (requestHandling (fun s p -> s.CSharpMetadata (p)))

    LanguageServerProtocol.Server.start requestHandlings
                                        input
                                        output
                                        CSharpLspClient
                                        (fun lspClient -> new CSharpLspServer(lspClient))

let start () =
    try
        let result = startCore ()
        int result
    with
    | _ex ->
        // logger.error (Log.setMessage "Start - LSP mode crashed" >> Log.addExn ex)
        3