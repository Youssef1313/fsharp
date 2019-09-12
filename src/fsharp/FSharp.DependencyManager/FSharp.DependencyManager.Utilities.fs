// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.
namespace FSharp.DependencyManager

open System
open System.Collections
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Reflection
open System.Runtime.CompilerServices
open System.Runtime.Versioning

open Internal.Utilities.FSharpEnvironment

open Microsoft.DotNet.PlatformAbstractions
open Microsoft.Extensions.DependencyModel

#if !(NETSTANDARD || NETCOREAPP)
open Microsoft.Build.Evaluation
open Microsoft.Build.Framework
#endif

[<AttributeUsage(AttributeTargets.Assembly ||| AttributeTargets.Class , AllowMultiple = false)>]
type DependencyManagerAttribute() = inherit System.Attribute()

module Utilities =

    /// Return a string array delimited by commas
    /// Note that a quoted string is not going to be mangled into pieces. 
    let trimChars = [| ' '; '\t'; '\''; '\"' |]

    let inline private isNotQuotedQuotation (text: string) n = n > 0 && text.[n-1] <> '\\'

    let getOptions text =
        let split (option:string) =
            let pos = option.IndexOf('=')
            let stringAsOpt text =
                if String.IsNullOrEmpty(text) then None
                else Some text
            let nameOpt =
                if pos <= 0 then None
                else stringAsOpt (option.Substring(0, pos).Trim(trimChars).ToLowerInvariant())
            let valueOpt =
                let valueText =
                    if pos < 0 then option
                    else if pos < option.Length then
                        option.Substring(pos + 1)
                    else ""
                stringAsOpt (valueText.Trim(trimChars))
            nameOpt,valueOpt

        let last = String.length text - 1
        let result = ResizeArray()
        let mutable insideSQ = false
        let mutable start = 0
        let isSeperator c = c = ','
        for i = 0 to last do
            match text.[i], insideSQ with
            | c, false when isSeperator c ->                        // split when seeing a separator
                result.Add(text.Substring(start, i - start))
                insideSQ <- false
                start <- i + 1
            | _, _ when i = last ->
                result.Add(text.Substring(start, i - start + 1))
            | c, true when isSeperator c ->                         // keep reading if a separator is inside quotation
                insideSQ <- true
            | '\'', _ when isNotQuotedQuotation text i ->           // open or close quotation
                insideSQ <- not insideSQ                            // keep reading
            | _ -> ()

        result |> Seq.map(fun option -> split option)

    // Path to the directory containing the fsharp compilers
    let fsharpCompilerPath = Path.GetDirectoryName(typeof<DependencyManagerAttribute>.GetTypeInfo().Assembly.Location)

    let isRunningOnCoreClr =
        // We are running on dotnet core if the executing application has .runtimeconfig.json
        let depsJsonPath = Path.ChangeExtension(Assembly.GetEntryAssembly().Location, "deps.json")
        File.Exists(depsJsonPath)

    let isWindows = 
        match Environment.OSVersion.Platform with
        | PlatformID.Unix -> false
        | PlatformID.MacOSX -> false
        | _ -> true

    let dotnet =
        if isWindows then "dotnet.exe" else "dotnet"

    let sdks = "Sdks"

#if !(NETSTANDARD || NETCOREAPP)
    let msbuildExePath =
        // Find msbuild.exe when invoked from desktop compiler.
        // 1. Look relative to F# compiler location                 Normal retail build
        // 2. Use VSAPPDIR                                          Nightly when started from VS, or F5
        // 3. Use VSINSTALLDIR                                   -- When app is run outside of VS, and
        //                                                          is not copied relative to a vs install.
        let vsRootFromVSAPPIDDIR =
            let vsappiddir = Environment.GetEnvironmentVariable("VSAPPIDDIR")
            if not (String.IsNullOrEmpty(vsappiddir)) then
                Path.GetFullPath(Path.Combine(vsappiddir, "../.."))
            else
                null

        let roots = [|
            Path.GetFullPath(Path.Combine(fsharpCompilerPath, "../../../../.."))
            vsRootFromVSAPPIDDIR
            Environment.GetEnvironmentVariable("VSINSTALLDIR")
            |]

        let msbuildPath root = Path.GetFullPath(Path.Combine(root, "MSBuild/Current/Bin/MSBuild.exe"))

        let msbuildPathExists root =
            if String.IsNullOrEmpty(root) then
                false
            else
                File.Exists(msbuildPath root)

        let msbuildOption rootOpt =
            match rootOpt with
            | Some root -> Some (msbuildPath root)
            | _ -> None

        roots |> Array.tryFind(fun root -> msbuildPathExists root) |> msbuildOption
#else
    let dotnetHostPath =
        if isRunningOnCoreClr then
            match (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")) with
            | value when not (String.IsNullOrEmpty(value)) -> Some value                           // Value set externally
            | _ ->
                let main = Process.GetCurrentProcess().MainModule
                if main.ModuleName.StartsWith("dotnet") then
                    Some main.FileName
                else
                    None
            else
                None
#endif
    let executeBuild pathToExe arguments =
        match pathToExe with
        | Some path ->
            let psi = ProcessStartInfo()
            psi.FileName <- path
            psi.RedirectStandardOutput <- false
            psi.RedirectStandardError <- false
            psi.Arguments <- arguments
            psi.CreateNoWindow <- true
            psi.UseShellExecute <- false

            use p = new Process()
            p.StartInfo <- psi
            p.Start() |> ignore
            p.WaitForExit()
            p.ExitCode = 0

        | None -> false

    let buildProject projectPath binLogging =

        let binLoggingArguments =
            match binLogging with
            | true -> "-bl"
            | _ -> ""

        let arguments prefix =
            sprintf "%s -restore %s %c%s%c /t:FSI-PackageManagement" prefix binLoggingArguments '\"' projectPath '\"'

        let succeeded =
#if !(NETSTANDARD || NETCOREAPP)
            // The Desktop build uses "msbuild" to build
            executeBuild msbuildExePath (arguments "")
#else
            // The coreclr uses "dotnet msbuild" to build
            executeBuild dotnetHostPath (arguments "msbuild")
#endif
        let outputFile = projectPath + ".fsx"
        let resultOutFile = if succeeded && File.Exists(outputFile) then Some outputFile else None
        succeeded, resultOutFile

    // Generate a project files for dependencymanager projects
    let generateLibrarySource = @"// Generated dependencymanager library
namespace lib"

    let generateProjectBody = @"
<Project Sdk='Microsoft.NET.Sdk'>
  <PropertyGroup>
    <TargetFramework>$(TARGETFRAMEWORK)</TargetFramework>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include='Library.fs' />
  </ItemGroup>
$(PACKAGEREFERENCES)

  <Target Name='CollectFSharpDesignTimeTools' BeforeTargets='BeforeCompile' DependsOnTargets='_GetFrameworkAssemblyReferences'>
    <ItemGroup>
      <PropertyNames Include = ""Pkg$([System.String]::Copy('%(PackageReference.FileName)').Replace('.','_'))"" Condition = "" '%(PackageReference.IsFSharpDesignTimeProvider)' == 'true' and '%(PackageReference.Extension)' == '' ""/>
      <PropertyNames Include = ""Pkg$([System.String]::Copy('%(PackageReference.FileName)%(PackageReference.Extension)').Replace('.','_'))"" Condition = "" '%(PackageReference.IsFSharpDesignTimeProvider)' == 'true' and '%(PackageReference.Extension)' != '' ""/>
      <FscCompilerTools Include = ""$(%(PropertyNames.Identity))"" />
    </ItemGroup>
  </Target>

  <Target Name=""PackageFSharpDesignTimeTools"" DependsOnTargets=""_GetFrameworkAssemblyReferences"">
    <PropertyGroup>
      <FSharpDesignTimeProtocol Condition = "" '$(FSharpDesignTimeProtocol)' == '' "">fsharp41</FSharpDesignTimeProtocol>
      <FSharpToolsDirectory Condition = "" '$(FSharpToolsDirectory)' == '' "">tools</FSharpToolsDirectory>
    </PropertyGroup>

    <Error Text=""'$(FSharpToolsDirectory)' is an invalid value for 'FSharpToolsDirectory' valid values are 'typeproviders' and 'tools'."" Condition=""'$(FSharpToolsDirectory)' != 'typeproviders' and '$(FSharpToolsDirectory)' != 'tools'"" />
    <Error Text=""The 'FSharpDesignTimeProtocol'  property can be only 'fsharp41'"" Condition=""'$(FSharpDesignTimeProtocol)' != 'fsharp41'"" />

    <ItemGroup>
      <_ResolvedOutputFiles
          Include=""%(_ResolvedProjectReferencePaths.RootDir)%(_ResolvedProjectReferencePaths.Directory)/**/*""
          Exclude=""%(_ResolvedProjectReferencePaths.RootDir)%(_ResolvedProjectReferencePaths.Directory)/**/FSharp.Core.dll;%(_ResolvedProjectReferencePaths.RootDir)%(_ResolvedProjectReferencePaths.Directory)/**/System.ValueTuple.dll""
          Condition=""'%(_ResolvedProjectReferencePaths.IsFSharpDesignTimeProvider)' == 'true'"">
        <NearestTargetFramework>%(_ResolvedProjectReferencePaths.NearestTargetFramework)</NearestTargetFramework>
      </_ResolvedOutputFiles>

      <_ResolvedOutputFiles
          Include=""@(BuiltProjectOutputGroupKeyOutput)""
          Condition=""'$(IsFSharpDesignTimeProvider)' == 'true' and '%(BuiltProjectOutputGroupKeyOutput->Filename)%(BuiltProjectOutputGroupKeyOutput->Extension)' != 'FSharp.Core.dll' and '%(BuiltProjectOutputGroupKeyOutput->Filename)%(BuiltProjectOutputGroupKeyOutput->Extension)' != 'System.ValueTuple.dll'"">
        <NearestTargetFramework>$(TargetFramework)</NearestTargetFramework>
      </_ResolvedOutputFiles>

      <TfmSpecificPackageFile Include=""@(_ResolvedOutputFiles)"">
         <PackagePath>$(FSharpToolsDirectory)/$(FSharpDesignTimeProtocol)/%(_ResolvedOutputFiles.NearestTargetFramework)/%(_ResolvedOutputFiles.FileName)%(_ResolvedOutputFiles.Extension)</PackagePath>
      </TfmSpecificPackageFile>

    </ItemGroup>
  </Target>

  <Target Name='ComputePackageRoots'
          BeforeTargets='CoreCompile;FSI-PackageManagement'
          DependsOnTargets='CollectPackageReferences'>
      <ItemGroup>
        <FsxResolvedFile Include='@(ResolvedCompileFileDefinitions)'>
           <PackageRootProperty>Pkg$([System.String]::Copy('%(ResolvedCompileFileDefinitions.NugetPackageId)').Replace('.','_'))</PackageRootProperty>
           <PackageRoot>$(%(FsxResolvedFile.PackageRootProperty))</PackageRoot>
           <InitializeSourcePath>$(%(FsxResolvedFile.PackageRootProperty))\content\%(ResolvedCompileFileDefinitions.FileName)%(ResolvedCompileFileDefinitions.Extension).fsx</InitializeSourcePath>
        </FsxResolvedFile>
      </ItemGroup>
  </Target>

  <Target Name='FSI-PackageManagement' DependsOnTargets='ResolvePackageAssets'>
    <ItemGroup>
      <ReferenceLines Remove='@(ReferenceLines)' />
      <ReferenceLines Include='// Generated from #r ""nuget:Package References""' />
      <ReferenceLines Include='// ============================================' />
      <ReferenceLines Include='//' />
      <ReferenceLines Include='// DOTNET_HOST_PATH:($(DOTNET_HOST_PATH))' />
      <ReferenceLines Include='// MSBuildSDKsPath:($(MSBuildSDKsPath))' />
      <ReferenceLines Include='// MSBuildExtensionsPath:($(MSBuildExtensionsPath))' />
      <ReferenceLines Include='//' />
      <ReferenceLines Include='#r @""%(FsxResolvedFile.HintPath)""'                 Condition = ""%(FsxResolvedFile.NugetPackageId) != 'Microsoft.NETCore.App' and %(FsxResolvedFile.NugetPackageId) != 'FSharp.Core' and %(FsxResolvedFile.NugetPackageId) != 'System.ValueTuple' and Exists('%(FsxResolvedFile.HintPath)')"" />
      <ReferenceLines Include='//' />
      <ReferenceLines Include='#load @""%(FsxResolvedFile.InitializeSourcePath)""'  Condition = ""%(FsxResolvedFile.NugetPackageId) != 'Microsoft.NETCore.App' and %(FsxResolvedFile.NugetPackageId) != 'FSharp.Core' and %(FsxResolvedFile.NugetPackageId) != 'System.ValueTuple' and Exists('%(FsxResolvedFile.InitializeSourcePath)')"" />
    </ItemGroup>

    <WriteLinesToFile Lines='@(ReferenceLines)' File='$(MSBuildProjectFullPath).fsx' Overwrite='True' WriteOnlyWhenDifferent='True' />
    <ItemGroup>
      <FileWrites Include='$(MSBuildProjectFullPath).fsx' />
    </ItemGroup>
  </Target>

</Project>"
