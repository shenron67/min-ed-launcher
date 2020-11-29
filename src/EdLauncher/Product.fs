namespace EdLauncher
    module Product =
        open System
        open System.Diagnostics
        open System.IO
        open System.Runtime.InteropServices
        open EdLauncher.Types
        
        let createArgString vr (lang: string option) edSession machineId timestamp watchForCrashes platform hashFile (product:ProductDetails) =
            let targetOptions = String.Join(" ", [
                if lang.IsSome then "/language " + lang.Value 
                match platform, product.SteamAware with
                    | Steam, true -> "/steam"
                    | Epic _, _ ->
                        let refresh = edSession.PlatformToken.GetRefreshToken() |> Option.defaultValue ""
                        $"\"EpicToken {refresh}\""
                    | _, _ -> ()
                match vr with
                    | Vr -> "/vr"
                    | _ -> "/novr"
                if not (String.IsNullOrEmpty(product.GameArgs)) then product.GameArgs ])
            let online =
                match product.Mode with
                | Offline -> false
                | Online -> true
            let serverToken = if online then sprintf "ServerToken %s %s %s" edSession.MachineToken (edSession.Token) product.ServerArgs else ""
            let combined = sprintf "\"%s\" %s" serverToken targetOptions
            let fullExePath = Path.Combine(product.Directory, product.Executable)
            let exeHash = fullExePath |> hashFile |> Result.map Hex.toString |> Result.map (fun p -> p.ToUpperInvariant()) |> Result.defaultValue ""
            if watchForCrashes && online then
                let version = product.Version.ToString()
                sprintf "/Executable \"%s\" /ExecutableArgs %s /MachineToken %s /Version %s /AuthToken %s /MachineId %s /Time %s /ExecutableHash %s"
                    fullExePath combined edSession.MachineToken version (edSession.Token) machineId (timestamp.ToString()) exeHash
            else
                combined
                
        type RunnableProduct =
            { Executable: FileInfo
              Version: Version
              SteamAware: bool
              Mode: ProductMode
              ServerArgs: string }
        let validateForRun launcherDir watchForCrashes (product: ProductDetails) =
            let productFullPath = Path.Combine(product.Directory, product.Executable)
            let watchDogFullPath = if product.UseWatchDog64 then Path.Combine(launcherDir, "WatchDog64.exe") else Path.Combine(launcherDir, "WatchDog.exe") 
            if not (File.Exists(productFullPath)) then
                Error <| sprintf "Unable to find product exe at '%s'" productFullPath
            elif watchForCrashes && not (File.Exists(watchDogFullPath)) then
                Error <| sprintf "Unable to find watchdog exe at '%s'" watchDogFullPath
            else
                let exePath = if watchForCrashes then watchDogFullPath else productFullPath
                Ok { Executable = FileInfo(exePath)
                     Version = product.Version
                     SteamAware = product.SteamAware
                     Mode = product.Mode
                     ServerArgs = product.ServerArgs }
            
        let isRunning (product:RunnableProduct) =
            let exeName = product.Executable.Name
            
            if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
                Process.GetProcessesByName(exeName).Length > 0 // TODO: check that this is true when running on Windows
            else
                Process.GetProcesses() // When running via wine, process name can be truncated and the main module is wine so check all module names
                |> Array.exists (fun p ->
                    p.Modules
                    |> Seq.cast<ProcessModule>
                    |> Seq.exists (fun m -> m.ModuleName = exeName))

        type RunResult = Ok of Process | AlreadyRunning | Error of exn
        let run proton args (product:RunnableProduct)  =
            if isRunning product then
                AlreadyRunning
            else
                let fileName, arguments =
                    match proton with
                    | Some (path, action) -> "python3", sprintf "\"%s\" %s \"%s\" %s" path action product.Executable.FullName args
                    | None -> product.Executable.FullName, args
                
                let startInfo = ProcessStartInfo()
                startInfo.FileName <- fileName
                startInfo.WorkingDirectory <- product.Executable.DirectoryName
                startInfo.Arguments <- arguments
                startInfo.CreateNoWindow <- true
                startInfo.UseShellExecute <- false
                startInfo.RedirectStandardOutput <- true
                startInfo.RedirectStandardError <- true
                
                try
                    Process.Start(startInfo) |> Ok
                with
                | e -> Error e