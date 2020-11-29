module EdLauncher.Log

open System
open System.IO
open System.Text.RegularExpressions
open Serilog
open Serilog.Configuration
open Serilog.Events
open Serilog.Formatting
open Serilog.Formatting.Display
open Serilog.Sinks.SystemConsole.Themes

type EpicScrubber(mainFormatter: ITextFormatter) = // https://github.com/serilog/serilog/issues/938#issuecomment-383440607
    let scubber = Regex(@"[a-zA-Z0-9-_]{24,}\.[a-zA-Z0-9-_]{24,}\.[a-zA-Z0-9-_]{24,}|[a-z0-9]{32}", RegexOptions.IgnoreCase)
    
    interface ITextFormatter with
        member this.Format(logEvent, output) =
            use stringWriter = new StringWriter()
            mainFormatter.Format(logEvent, stringWriter)
            let input = stringWriter.ToString();
            let sanitized = scubber.Replace(input, (fun m -> $"{m.Value.[..2]}...{m.Value.[^2..]}"))

            try
                output.Write(sanitized);
            with
            | e -> Log.Error(e, "Epic scrubber broke");

type LoggerSinkConfiguration with
    member this.ScrubbedConsole(restrictedToMinimumLevel) = // https://github.com/serilog/serilog-sinks-console/issues/41#issuecomment-395246313
        // HACK: Use reflection since the OutputTemplateRenderer is marked as internal and can't get a reference
        //       to the console sink's ITextFormatter
        typedefof<ConsoleLoggerConfigurationExtensions>.Assembly.GetTypes()
        |> Seq.tryFind (typedefof<ITextFormatter>.IsAssignableFrom)
        |> function
           | Some t ->
               t.GetConstructors()
               |> Seq.tryHead
               |> function
                  | Some c ->
                    try
                        let theme = if Console.IsOutputRedirected || Console.IsErrorRedirected
                                    then ConsoleTheme.None
                                    else upcast AnsiConsoleTheme.Code
                        let textFormatter = c.Invoke([| theme; "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}"; null |]) :?> ITextFormatter
                        let formatter = EpicScrubber(textFormatter)
                        this.Console(formatter, restrictedToMinimumLevel);
                    with e -> raise <| exn $"HACK: An error was thrown while trying to construct '{t.FullName}'. This is most likely because the constructor parameters have changed in number or order. Look at the source code for '{typedefof<ConsoleLoggerConfigurationExtensions>.Assembly.FullName}'." 
                      
                  | None -> raise <| exn $"HACK: There was no constructor found on type '{t.FullName}'. This could be because we have selected the incorrect type, or the constructor is not private or something else. Look at the source code for '{typedefof<ConsoleLoggerConfigurationExtensions>.Assembly.FullName}'." 
           | None -> raise <| exn $"HACK: No type found in '{typedefof<ConsoleLoggerConfigurationExtensions>.Assembly.FullName}' that implements {nameof(ITextFormatter)}." 
    member this.ScrubbedFile(path, restrictedToMinimumLevel) =
        let formatter = EpicScrubber(MessageTemplateTextFormatter("{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}", null))
        this.File(formatter = formatter, path=path, restrictedToMinimumLevel=restrictedToMinimumLevel)

let logger =
  let consoleLevel =
#if DEBUG
      LogEventLevel.Debug
#else      
      LogEventLevel.Information
#endif
  LoggerConfiguration()
    .MinimumLevel.Verbose()
    .WriteTo.ScrubbedConsole(consoleLevel)
    .WriteTo.ScrubbedFile("ed.log", LogEventLevel.Verbose)
    .CreateLogger()
    
let private write level msg =
    logger.Write(level, msg)
let private writeExn exn level msg =
    logger.Write(level, msg)
    
let private log level format = Printf.ksprintf (write level) format
let private logExn level exn format = Printf.ksprintf (writeExn exn level) format

let debugf format = log LogEventLevel.Debug format
let debug msg = debugf "%s" msg
let infof format = log LogEventLevel.Information format
let info msg = infof "%s" msg
let warnf format = log LogEventLevel.Warning format
let warn msg = warnf "%s" msg
let errorf format = log LogEventLevel.Error format
let error msg = errorf "%s" msg
let exnf e format = logExn LogEventLevel.Fatal e format
let exn e msg = exnf e "%s" msg