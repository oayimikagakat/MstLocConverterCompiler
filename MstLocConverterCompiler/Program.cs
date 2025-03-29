using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using MemoryPack;

class Program
{
    static void Main(string[] args)
    {
        string dummyPath = Path.GetFullPath(args[0]);
        string definitionPath = Path.GetFullPath(args[1]);

        ExtractDefinition(dummyPath, definitionPath);
    }

    static string GetTypeName(Type type, Assembly assembly)
    {
        if (type.IsEnum)
        {
            return "int";
        }
        if (type.IsGenericType)
        {
            string typeName = type.GetGenericTypeDefinition().Name;
            int backtickIdx = typeName.IndexOf('`');
            if (backtickIdx > 0)
                typeName = typeName.Remove(backtickIdx);
            Type[] genericArguments = type.GetGenericArguments();
            return $"{typeName}<{string.Join(", ", genericArguments.Select(t => GetTypeName(t, assembly)))}>";
        }
        else
        {
            return type.Name;
        }
    }

    static void ExtractDefinition(string dummyPath, string outputPath)
    {
        StringBuilder builder = new StringBuilder();
        builder.AppendLine(@"using System;
using System.IO;
using System.Collections.Generic;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using MemoryPack;


class Program
{
    static void Main(string[] args)
    {
        string label = args[0];
        string sourcePath = Path.GetFullPath(args[1]);

        if (label == ""masterdata"")
        {
            ConvertMemoryPackToJson<MasterData>(sourcePath, label);
        }
        else if (label == ""localizetext"")
        {
            ConvertMemoryPackToJson<Dictionary<string, Dictionary<int, string>>>(sourcePath, label);
        }
    }

    static void ConvertMemoryPackToJson<T>(string basePath, string label)
    {
        string inputPath = Path.Combine(basePath, label);
        string outputPath = Path.Combine(basePath, label + "".json"");
        try
        {
            byte[] dataBytes = File.ReadAllBytes(inputPath);
            T? data = MemoryPackSerializer.Deserialize<T>(dataBytes);
            if (data != null)
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };

                string json = JsonSerializer.Serialize(data, options);
                File.WriteAllText(outputPath, json);
                Console.WriteLine($""Converted {Path.GetFileName(outputPath)}"");
            }
            else
            {
                Console.WriteLine($""Failed to deserialize {Path.GetFileName(outputPath)}"");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($""Error while processing {Path.GetFileName(outputPath)}: {ex.Message}"");
        }
    }
}");

        var runtimePaths = new List<string>(Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"));
        var dummyPaths = new List<string>(Directory.GetFiles(dummyPath, "*.dll"));
        var paths = runtimePaths.Concat(dummyPaths);
        var resolver = new PathAssemblyResolver(paths);
        var ctx = new MetadataLoadContext(resolver);

        var assembly = ctx.LoadFromAssemblyPath(Path.Combine(dummyPath, "PRISM.Definitions.dll"));

        var typeNames = new HashSet<string>();
        var mstClassNames = new List<string>();

        foreach (System.Reflection.TypeInfo t in assembly.GetTypes().Where(t => t.GetInterfaces().Any(i => i.Name == "IMemoryPackable`1")))
        {
            mstClassNames.Add(t.Name);

            builder.AppendLine("[MemoryPackable]");
            builder.AppendLine($"public partial class {t.Name}");
            builder.AppendLine("{");
            foreach (PropertyInfo m in t.GetMembers().Where(m => m.MemberType == MemberTypes.Property).Cast<PropertyInfo>())
            {
                var attributes = m.GetCustomAttributesData();
                if (attributes.Any(c => c.AttributeType.Name == "MemoryPackIgnoreAttribute"))
                    continue;

                builder.Append("\tpublic ");
                var typeName = GetTypeName(m.PropertyType, assembly);
                typeNames.Add(typeName);
                builder.Append(typeName);
                builder.Append($" {m.Name} {{ get; ");

                var setMethod = m.GetSetMethod(true);
                if (setMethod != null)
                {
                    if (setMethod.IsPrivate)
                        builder.Append("private ");
                    builder.Append("set; ");
                }
                builder.AppendLine("}");

            }
            builder.AppendLine("}");
            builder.AppendLine("");
        }

        builder.AppendLine("");
        builder.AppendLine("[MemoryPackable]");
        builder.AppendLine("public partial class MasterData");
        builder.AppendLine("{");

        foreach (var name in mstClassNames)
        {
            builder.AppendLine($"\tpublic List<{name}> {name} {{ get; private set; }}");
        }
        builder.AppendLine("}");

        GenerateAndBuildProject(outputPath, builder.ToString());
    }

    static void GenerateAndBuildProject(string outputPath, string sourceCode)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "MstLocConverter_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            File.WriteAllText(Path.Combine(tempDir, "Program.cs"), sourceCode);

            string csprojContent = @"<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <PublishSingleFile>true</PublishSingleFile>
    <SelfContained>true</SelfContained>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <PublishReadyToRun>true</PublishReadyToRun>
    <DebugType>none</DebugType>
    <DebugSymbols>false</DebugSymbols>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include=""MemoryPack"" Version=""1.10.0"" />
  </ItemGroup>
</Project>";

            File.WriteAllText(Path.Combine(tempDir, "MstLocConverter.csproj"), csprojContent);

            Directory.CreateDirectory(outputPath);

            string finalExePath = Path.Combine(outputPath, "MstLocConverter.exe");
            Console.WriteLine("Building application...");

            var processInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"publish -c Release -o \"{outputPath}\" --nologo",
                WorkingDirectory = tempDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (var process = Process.Start(processInfo))
            {
                if (process == null)
                {
                    throw new Exception("Failed to start dotnet publish process");
                }

                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();

                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    Console.WriteLine("Build output:");
                    Console.WriteLine(output);
                    Console.WriteLine("Build errors:");
                    Console.WriteLine(error);
                    throw new Exception($"dotnet publish failed with exit code {process.ExitCode}");
                }
            }

            if (File.Exists(finalExePath))
            {
                Console.WriteLine($"Successfully created self-contained application: {finalExePath}");
            }
            else
            {
                throw new Exception($"Build completed but executable not found at {finalExePath}");
            }
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, true);
            }
            catch
            {
                Console.WriteLine($"Note: Could not clean up temporary directory: {tempDir}");
            }
        }
    }
}