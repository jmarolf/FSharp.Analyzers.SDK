namespace FSharp.Analyzers.SDK

open System
open System.IO
open System.Reflection
open System.Runtime.Loader
open McMaster.NETCore.Plugins
open System.Collections.Concurrent

module Client =

  let internal attributeName = "AnalyzerAttribute"

  let internal isAnalyzer (mi: MemberInfo) =
    mi.GetCustomAttributes true
    |> Seq.exists (fun n -> n.GetType().Name = attributeName)

  let internal analyzerFromMember (mi: MemberInfo) : Analyzer option =
    let inline unboxAnalyzer v =
      if isNull v then
        failwith "Analyzer is null"
      else unbox v
    let getAnalyzerFromMemberInfo mi =
      match box mi with
      | :? FieldInfo as m ->
        if m.FieldType = typeof<Analyzer> then Some(m.GetValue(null) |> unboxAnalyzer)
        else None
      | :? MethodInfo as m ->
        if m.ReturnType = typeof<Analyzer>
          then Some(m.Invoke(null, null) |> unboxAnalyzer)
        elif m.ReturnType.FullName.StartsWith "Microsoft.FSharp.Collections.FSharpList`1[[FSharp.Analyzers.SDK.Message" then
          try
            let x : Analyzer = fun ctx ->
              try
                m.Invoke(null, [|ctx|]) |> unbox
              with
              | ex ->
                printfn "Error while executing Analyzer from %s.%s" m.DeclaringType.Name m.Name
                printfn "%A" ex
                []
            Some x
          with
          | ex -> None
        else None
      | :? PropertyInfo as m ->
        if m.PropertyType = typeof<Analyzer> then Some(m.GetValue(null, null) |> unboxAnalyzer)
        else None
      | _ -> None
    if isAnalyzer mi then getAnalyzerFromMemberInfo mi else None

  let internal analyzersFromType (t: Type) =
    let asMembers x = Seq.map (fun m -> m :> MemberInfo) x
    let bindingFlags = BindingFlags.Public ||| BindingFlags.Static

    let members =
      [ t.GetTypeInfo().GetMethods bindingFlags |> asMembers
        t.GetTypeInfo().GetProperties bindingFlags |> asMembers
        t.GetTypeInfo().GetFields bindingFlags |> asMembers ]
      |> Seq.collect id
    members
    |> Seq.choose analyzerFromMember
    |> Seq.toList

  let registeredAnalyzers: ConcurrentDictionary<string, Analyzer list> = ConcurrentDictionary()

  ///Loads into private state any analyzers defined in any assembly
  ///matching `*Analyzer*.dll` in given directory (and any subdirectories)
  ///Returns number of found dlls matching `*Analyzer*.dll` and number of registered analyzers
  let loadAnalyzers (dir: string): (int*int) =
    if Directory.Exists dir then
      let analyzerAssemblies =
          Directory.GetFiles(dir, "*Analyzer*.dll", SearchOption.AllDirectories)
          |> Array.choose (fun analyzerDll ->
            try
              // loads an assembly and all of it's dependencies
              let analyzerLoader = PluginLoader.CreateFromAssemblyFile(analyzerDll, fun config -> config.DefaultContext <- AssemblyLoadContext.Default; config.PreferSharedTypes <- true)
              Some (analyzerDll, analyzerLoader.LoadDefaultAssembly())
            with
            | _ -> None)

      let analyzers =
        analyzerAssemblies
        |> Array.map (fun (path,assembly) ->
          let analyzers = assembly.GetExportedTypes() |> Seq.collect (analyzersFromType)
          path, analyzers)

      analyzers
      |> Seq.iter (fun (path, analyzers) ->
        let analyzers = Seq.toList analyzers
        registeredAnalyzers.AddOrUpdate(path, analyzers, (fun _ _ -> analyzers))
        |> ignore
      )

      Seq.length analyzers,
      analyzers |> Seq.collect (snd) |> Seq.length
    else
      0,0

  ///Runs all registered analyzers for given context (file).
  ///Returns list of messages
  let runAnalyzers (ctx: Context) : Message list =
    let analyzers = registeredAnalyzers.Values |> Seq.collect id
    analyzers
    |> Seq.collect (fun analyzer -> analyzer ctx)
    |> Seq.toList