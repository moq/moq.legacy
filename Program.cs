using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace legacynator
{
    class Program
    {
        static async System.Threading.Tasks.Task<int> Main(string[] args)
        {
            var projectFile = args.FirstOrDefault() ?? Path.Combine(ThisAssembly.Project.MSBuildProjectDirectory, @"moq4\src\Moq\Moq.csproj");
            var projectDir = Path.GetDirectoryName(projectFile);

            if (Directory.Exists(Path.Combine(projectDir, "obj")))
                Directory.Delete(Path.Combine(projectDir, "obj"), true);

            if (Directory.Exists(Path.Combine(projectDir, "bin")))
                Directory.Delete(Path.Combine(projectDir, "bin"), true);

            var result = Build(projectFile, "Restore");
            if (result.OverallResult != BuildResultCode.Success)
            {
                Debug.Assert(false, "Could not restore successfully.");
                return -1;
            }

            var workspace = MSBuildWorkspace.Create(new Dictionary<string, string> { { "TargetFramework", "netstandard2.0" } });
            var project = await workspace.OpenProjectAsync(projectFile);

            foreach (var doc in project.Documents)
            {
                var root = await doc.GetSyntaxRootAsync();
                if (root == null)
                    continue;

                var newRoot = new LegacyNamespaceRewriter().Visit(root);

                File.WriteAllText(doc.FilePath, newRoot.ToFullString(), Encoding.UTF8);
            }

            result = Build(projectFile, "Build");
            if (result.OverallResult != BuildResultCode.Success)
            {
                Debug.Assert(false, "Could not build successfully.");
                return -1;
            }

            result = Build(projectFile, "Pack");
            if (result.OverallResult != BuildResultCode.Success)
            {
                Debug.Assert(false, "Could not pack successfully.");
                return -1;
            }

            return 0;
        }

        class LegacyNamespaceRewriter : CSharpSyntaxRewriter
        {
            public LegacyNamespaceRewriter() : base(true)
            {
            }

            public override SyntaxNode? VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
            {
                var name = node.Name.ToString();
                if (name.StartsWith("Moq") && !name.StartsWith("Moq.Legacy"))
                    return base.VisitNamespaceDeclaration(node.WithName(ParseName(node.Name.ToString().Replace("Moq", "Moq.Legacy"))));

                return base.VisitNamespaceDeclaration(node);
            }

            public override SyntaxNode? VisitUsingDirective(UsingDirectiveSyntax node)
            {
                var name = node.Name.ToString();
                if (name.StartsWith("Moq") && !name.StartsWith("Moq.Legacy"))
                    return base.VisitUsingDirective(node.WithName(ParseName(node.Name.ToString().Replace("Moq", "Moq.Legacy"))));

                return base.VisitUsingDirective(node);
            }

            public override SyntaxNode? VisitFieldDeclaration(FieldDeclarationSyntax node)
            {
                if (IsQualifiedMoq(node.Declaration.Type))
                    return base.VisitFieldDeclaration(node.WithDeclaration(node.Declaration.WithType(AsLegacy(node.Declaration.Type))));

                return base.VisitFieldDeclaration(node);
            }

            public override SyntaxNode? VisitParameter(ParameterSyntax node)
            {
                if (IsQualifiedMoq(node.Type))
                    return base.VisitParameter(node.WithType(AsLegacy(node.Type!)));

                return base.VisitParameter(node);
            }

            public override SyntaxNode? VisitXmlElement(XmlElementSyntax node)
            {
                return base.VisitXmlElement(node);
            }

            public override SyntaxNode? VisitXmlEmptyElement(XmlEmptyElementSyntax node)
            {
                return base.VisitXmlEmptyElement(node);
            }

            public override SyntaxNode? VisitXmlCrefAttribute(XmlCrefAttributeSyntax node)
            {
                if (node.Cref is QualifiedCrefSyntax qc)
                {
                    if (IsQualifiedMoq(qc.Container))
                        return base.VisitXmlCrefAttribute(node.WithCref(QualifiedCref(AsLegacy(qc.Container), qc.Member)));
                    if (qc.Container.ToString() == "Moq")
                        return base.VisitXmlCrefAttribute(node.WithCref(QualifiedCref(IdentifierName("Moq.Legacy"), qc.Member)));
                }

                return base.VisitXmlCrefAttribute(node);
            }

            public override SyntaxNode? VisitCrefParameter(CrefParameterSyntax node)
            {
                if (IsQualifiedMoq(node.Type))
                    return base.VisitCrefParameter(node.WithType(AsLegacy(node.Type)));

                return base.VisitCrefParameter(node);
            }

            public override SyntaxNode? VisitBaseList(BaseListSyntax node)
            {
                if (node.ChildNodes().OfType<SimpleBaseTypeSyntax>().Any(x => IsQualifiedMoq(x.Type)))
                {
                    return base.VisitBaseList(BaseList(SeparatedList<BaseTypeSyntax>(node.ChildNodes()
                        .OfType<SimpleBaseTypeSyntax>().Select(node => node.WithType(AsLegacy(node.Type))))));
                }

                return base.VisitBaseList(node);
            }

            bool IsQualifiedMoq(TypeSyntax? type)
                => type is QualifiedNameSyntax name && name.Left.ToString() == "Moq" && name.Right.ToString() != "Legacy";

            TypeSyntax AsLegacy(TypeSyntax type)
            {
                if (type is not QualifiedNameSyntax name ||
                    name.Left.ToString() != "Moq" ||
                    name.Right.ToString() == "Legacy")
                    return type;

                return name.WithLeft(IdentifierName("Moq.Legacy"));
            }
        }

        static BuildResult Build(string project, params string[] targets)
        {
            var manager = BuildManager.DefaultBuildManager;
            var binlog = $"{Path.GetFileNameWithoutExtension(project)}-{string.Join("-", targets)}.binlog";
            var parameters = new BuildParameters
            {
                LogInitialPropertiesAndItems = true,
                LogTaskInputs = true,
                Loggers = new ILogger[]
                {
                    new Microsoft.Build.Logging.ConsoleLogger(LoggerVerbosity.Minimal),
                    new Microsoft.Build.Logging.BinaryLogger
                    {
                        CollectProjectImports = Microsoft.Build.Logging.BinaryLogger.ProjectImportsCollectionMode.None,
                        Parameters = binlog,
                        Verbosity = LoggerVerbosity.Diagnostic
                    }
                }
            };

            var properties = new Dictionary<string, string>()
            {
                { nameof(ThisAssembly.Project.NuGetRestoreTargets), ThisAssembly.Project.NuGetRestoreTargets },
                { nameof(ThisAssembly.Project.NuGetTargets), ThisAssembly.Project.NuGetTargets},
                { "AssemblyName", "Moq.Legacy" },
                { "RootNamespace", "Moq.Legacy" },
                { "PackageId", "Moq.Legacy" },
                { "Title", "Moq.Legacy: use v4 API SxS with v5" },
            };

            var instance = new ProjectInstance(project, properties, "Current");
            var request = new BuildRequestData(instance, targets);
            var result = manager.Build(parameters, request);
            if (result.OverallResult != BuildResultCode.Success)
                Process.Start(binlog);

            return result;
        }
    }
}
