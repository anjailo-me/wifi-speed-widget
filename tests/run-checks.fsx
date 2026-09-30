open System
open System.IO
open System.Reflection
open System.Runtime.Loader

let here = __SOURCE_DIRECTORY__
let repo = Path.GetFullPath(Path.Combine(here, ".."))
let tfm = "net10.0-windows10.0.19041.0"
let appDir = Path.Combine(repo, "WifiSpeedWidget", "bin", "Release", tfm)
let checksDir = Path.Combine(here, "SpeedlineChecks", "bin", "Release", tfm)

let outDir =
    match Environment.GetEnvironmentVariable "CHECKS_OUT" with
    | null | "" -> Path.Combine(here, "out")
    | d -> d

let desktop =
    let root = Path.Combine(Environment.GetEnvironmentVariable "ProgramFiles", "dotnet", "shared", "Microsoft.WindowsDesktop.App")
    let exact = Path.Combine(root, Environment.Version.ToString(3))

    if Directory.Exists exact then
        exact
    else
        Directory.GetDirectories root |> Array.maxBy (fun d -> Version(Path.GetFileName d))

type CheckContext() =
    inherit AssemblyLoadContext("speedline-checks", false)
    member val App: Assembly = null with get, set
    member val Checks: Assembly = null with get, set

    override this.Load(name: AssemblyName) : Assembly =
        match name.Name with
        | "WifiSpeedWidget" -> this.App
        | "SpeedlineChecks" -> this.Checks
        | n ->
            [ Path.Combine(desktop, n + ".dll"); Path.Combine(appDir, n + ".dll") ]
            |> List.tryFind File.Exists
            |> Option.map this.LoadFromAssemblyPath
            |> Option.toObj

    override this.LoadUnmanagedDll(name: string) : nativeint =
        let p = Path.Combine(desktop, name)
        if File.Exists p then this.LoadUnmanagedDllFromPath p else base.LoadUnmanagedDll name

let appDll = Path.Combine(appDir, "WifiSpeedWidget.dll")
let checksDll = Path.Combine(checksDir, "SpeedlineChecks.dll")

if not (File.Exists appDll) then
    failwithf "Build the app first: dotnet build WifiSpeedWidget\\WifiSpeedWidget.csproj -c Release (looked for %s)" appDll

if not (File.Exists checksDll) then
    failwithf "Build the checks first: dotnet build tests\\SpeedlineChecks\\SpeedlineChecks.csproj -c Release (looked for %s)" checksDll

Environment.SetEnvironmentVariable("SPEEDLINE_APP_DIR", appDir)
let ctx = CheckContext()
ctx.App <- ctx.LoadFromStream(new MemoryStream(File.ReadAllBytes appDll))
ctx.Checks <- ctx.LoadFromStream(new MemoryStream(File.ReadAllBytes checksDll))
printfn "checking %s %s" (ctx.App.GetName().Name) (ctx.App.GetName().Version.ToString())

let run = ctx.Checks.GetType("SpeedlineChecks.Harness").GetMethod("Run")

let code =
    try
        run.Invoke(null, [| box outDir |]) :?> int
    with :? TargetInvocationException as e ->
        printfn "CHECKS THREW: %s" (e.InnerException.ToString())
        2

exit code
