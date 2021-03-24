// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Razor.Extensions;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Razor;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.NET.Sdk.Razor.SourceGenerators
{
    [Generator]
    public partial class RazorSourceGenerator : ISourceGenerator
    {
        // Until the compiler supports granular caching for generators, we roll out our own simple caching implementation.
        // https://github.com/dotnet/roslyn/issues/51257 track the long-term resolution for this.
        private static readonly ConcurrentDictionary<Guid, IReadOnlyList<TagHelperDescriptor>> _tagHelperCache = new();

        private static readonly ConcurrentDictionary<string, SourceText> _sourceTextCache = new();

        private static readonly SourceText ProvideApplicationPartFactoryAttributeSourceText = GetProvideApplicationPartFactorySourceText();

        public void Initialize(GeneratorInitializationContext context)
        {
        }

        public void Execute(GeneratorExecutionContext context)
        {
            var razorContext = new RazorSourceGenerationContext(context);
            if (razorContext is null)
            {
                context.ReportDiagnostic(Diagnostic.Create(RazorDiagnostics.InvalidRazorContextComputedDescriptor, Location.None));
                return;
            }

            if (razorContext.RazorFiles.Count == 0 && razorContext.CshtmlFiles.Count == 0)
            {
                return;
            }

            HandleDebugSwitch(razorContext.WaitForDebugger);

            var tagHelpers = ResolveTagHelperDescriptors(context, razorContext);

            var projectEngine = RazorProjectEngine.Create(razorContext.Configuration, razorContext.FileSystem, b =>
            {
                b.Features.Add(new DefaultTypeNameFeature());
                b.SetRootNamespace(razorContext.RootNamespace);

                b.Features.Add(new ConfigureRazorCodeGenerationOptions(options =>
                {
                    options.SuppressMetadataSourceChecksumAttributes = !razorContext.GenerateMetadataSourceChecksumAttributes;
                }));

                b.Features.Add(new StaticTagHelperFeature { TagHelpers = tagHelpers, });
                b.Features.Add(new DefaultTagHelperDescriptorProvider());

                CompilerFeatures.Register(b);
                RazorExtensions.Register(b);

                b.SetCSharpLanguageVersion(((CSharpParseOptions)context.ParseOptions).LanguageVersion);
            });

            CodeGenerateRazorComponents(context, razorContext, projectEngine);
            GenerateViews(context, razorContext, projectEngine);
        }

        private void GenerateViews(GeneratorExecutionContext context, RazorSourceGenerationContext razorContext, RazorProjectEngine projectEngine)
        {
            var files = razorContext.CshtmlFiles;

            if (files.Count == 0)
            {
                return;
            }

            var arraypool = ArrayPool<(string, SourceText)>.Shared;
            var outputs = arraypool.Rent(files.Count);

            context.AddSource($"{context.Compilation.AssemblyName}.UnifiedAssembly.Info", ProvideApplicationPartFactoryAttributeSourceText);

            Parallel.For(0, files.Count, GetParallelOptions(context), i =>
            {
                var file = files[i];

                var codeDocument = projectEngine.Process(projectEngine.FileSystem.GetItem(file.NormalizedPath, FileKinds.Legacy));
                var csharpDocument = codeDocument.GetCSharpDocument();
                for (var j = 0; j < csharpDocument.Diagnostics.Count; j++)
                {
                    var razorDiagnostic = csharpDocument.Diagnostics[j];
                    var csharpDiagnostic = razorDiagnostic.AsDiagnostic();
                    context.ReportDiagnostic(csharpDiagnostic);
                }

                var generatedCode = csharpDocument.GeneratedCode;
                var hint = GetIdentifierFromPath(file.GeneratedOutputPath ?? file.NormalizedPath);
                outputs[i] = (hint, SourceText.From(generatedCode, Encoding.UTF8));
            });

            for (var i = 0; i < files.Count; i++)
            {
                var (hint, sourceText) = outputs[i];
                context.AddSource(hint, ResolveSourceTextFromCache(sourceText, hint));
            }

            arraypool.Return(outputs);
        }

        private static void CodeGenerateRazorComponents(GeneratorExecutionContext context, RazorSourceGenerationContext razorContext, RazorProjectEngine projectEngine)
        {
            var files = razorContext.RazorFiles;

            var arraypool = ArrayPool<(string, SourceText)>.Shared;
            var outputs = arraypool.Rent(files.Count);

            Parallel.For(0, files.Count, GetParallelOptions(context), i =>
            {
                var file = files[i];
                var projectItem = projectEngine.FileSystem.GetItem(file.NormalizedPath, FileKinds.Component);

                var codeDocument = projectEngine.Process(projectItem);
                var csharpDocument = codeDocument.GetCSharpDocument();
                for (var j = 0; j < csharpDocument.Diagnostics.Count; j++)
                {
                    var razorDiagnostic = csharpDocument.Diagnostics[j];
                    var csharpDiagnostic = razorDiagnostic.AsDiagnostic();
                    context.ReportDiagnostic(csharpDiagnostic);
                }

                var hint = GetIdentifierFromPath(file.NormalizedPath);

                var generatedCode = csharpDocument.GeneratedCode;
                outputs[i] = (hint, SourceText.From(generatedCode, Encoding.UTF8));
            });

            for (var i = 0; i < files.Count; i++)
            {
                var (hint, sourceText) = outputs[i];
                context.AddSource(hint, ResolveSourceTextFromCache(sourceText, hint));
            }

            arraypool.Return(outputs);
        }

        private static SourceText ResolveSourceTextFromCache(SourceText generatedSourceText, string hint)
        {
            var checksum = generatedSourceText.GetChecksum();
            var cachedSourceTextFound = _sourceTextCache.TryGetValue(hint, out var cachedSourceText);
            if (cachedSourceTextFound && cachedSourceText.GetChecksum().SequenceEqual(checksum))
            {
                return cachedSourceText;
            }
            else
            {
                _sourceTextCache[hint] = generatedSourceText;
                return generatedSourceText;
            }
        }

        private static IReadOnlyList<TagHelperDescriptor> ResolveTagHelperDescriptors(GeneratorExecutionContext GeneratorExecutionContext, RazorSourceGenerationContext razorContext)
        {
            var tagHelperFeature = new StaticCompilationTagHelperFeature(GeneratorExecutionContext);

            var parseOptions = (CSharpParseOptions)GeneratorExecutionContext.ParseOptions;
            var langVersion = parseOptions.LanguageVersion;

            var discoveryProjectEngine = RazorProjectEngine.Create(razorContext.Configuration, razorContext.FileSystem, b =>
            {
                b.Features.Add(new DefaultTypeNameFeature());
                b.Features.Add(new ConfigureRazorCodeGenerationOptions(options =>
                {
                    options.SuppressPrimaryMethodBody = true;
                    options.SuppressChecksum = true;
                }));

                b.SetRootNamespace(razorContext.RootNamespace);

                var metadataReferences = new List<MetadataReference>(GeneratorExecutionContext.Compilation.References);
                b.Features.Add(new DefaultMetadataReferenceFeature { References = metadataReferences });

                b.Features.Add(tagHelperFeature);
                b.Features.Add(new DefaultTagHelperDescriptorProvider());

                CompilerFeatures.Register(b);
                RazorExtensions.Register(b);

                b.SetCSharpLanguageVersion(langVersion);
            });

            var files = razorContext.RazorFiles;

            var results = ArrayPool<SyntaxTree>.Shared.Rent(files.Count);

            Parallel.For(0, files.Count, GetParallelOptions(GeneratorExecutionContext), i =>
            {
                var file = files[i];
                var codeGen = discoveryProjectEngine.Process(discoveryProjectEngine.FileSystem.GetItem(file.NormalizedPath, FileKinds.Component));
                var generatedCode = codeGen.GetCSharpDocument().GeneratedCode;

                results[i] = CSharpSyntaxTree.ParseText(
                    generatedCode,
                    options: parseOptions);
            });

            // Add declaration codegen to the compilation so we can perform discovery on it.
            var compilationWithDeclarationCodeGen = GeneratorExecutionContext.Compilation.AddSyntaxTrees(results.Take(files.Count));
            ArrayPool<SyntaxTree>.Shared.Return(results);

            tagHelperFeature.Compilation = compilationWithDeclarationCodeGen;

            tagHelperFeature.TargetAssembly = compilationWithDeclarationCodeGen.Assembly;
            var assemblyTagHelpers = tagHelperFeature.GetDescriptors();

            var refTagHelpers = GetTagHelperDescriptorsFromReferences(GeneratorExecutionContext, tagHelperFeature);

            var result = new List<TagHelperDescriptor>(refTagHelpers.Count + assemblyTagHelpers.Count);
            result.AddRange(assemblyTagHelpers);
            result.AddRange(refTagHelpers);

            return result;
        }

        private static IReadOnlyList<TagHelperDescriptor> GetTagHelperDescriptorsFromReferences(GeneratorExecutionContext context, StaticCompilationTagHelperFeature tagHelperFeature)
        {
            List<TagHelperDescriptor> tagHelperDescriptors = new();
            var compilation = context.Compilation;

            foreach (var reference in compilation.References)
            {
                if (compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly)
                {
                    var guid = reference.GetModuleVersionId(compilation);
                    IReadOnlyList<TagHelperDescriptor> descriptors = new List<TagHelperDescriptor>();

                    if (guid is Guid _guid)
                    {
                        if (!_tagHelperCache.TryGetValue(_guid, out descriptors))
                        {
                            tagHelperFeature.TargetAssembly = assembly;
                            descriptors = tagHelperFeature.GetDescriptors();
                            // Clear out the cache if it is growing too large. A
                            // simple compilation can include around ~300 references
                            // so give a little bit of buffer beyond this.
                            if (_tagHelperCache.Count > 400)
                            {
                                _tagHelperCache.Clear();
                            }
                            _tagHelperCache[_guid] = descriptors;
                        }
                    }
                    else
                    {
                        tagHelperFeature.TargetAssembly = assembly;
                        descriptors = tagHelperFeature.GetDescriptors();
                        context.ReportDiagnostic(Diagnostic.Create(
                            RazorDiagnostics.ReComputingTagHelpersDescriptor,
                            Location.None,
                            reference.Display));
                    }

                    tagHelperDescriptors.AddRange(descriptors);
                }
            }

            return tagHelperDescriptors;
        }

        private static SourceText GetProvideApplicationPartFactorySourceText()
        {
            var typeInfo = "Microsoft.AspNetCore.Mvc.ApplicationParts.ConsolidatedAssemblyApplicationPartFactory, Microsoft.AspNetCore.Mvc.Razor";
            var assemblyInfo = $@"[assembly: global::Microsoft.AspNetCore.Mvc.ApplicationParts.ProvideApplicationPartFactoryAttribute(""{typeInfo}"")]";
            return SourceText.From(assemblyInfo, Encoding.UTF8);
        }

        private static string GetIdentifierFromPath(string filePath)
        {
            var builder = new StringBuilder(filePath.Length);

            for (var i = 0; i < filePath.Length; i++)
            {
                builder.Append(filePath[i] switch
                {
                    ':' or '\\' or '/' => '_',
                    var @default => @default,
                });
            }

            return builder.ToString();
        }

        private static ParallelOptions GetParallelOptions(GeneratorExecutionContext generatorExecutionContext)
        {
            var options = new ParallelOptions { CancellationToken = generatorExecutionContext.CancellationToken };
            var isConcurrentBuild = generatorExecutionContext.Compilation.Options.ConcurrentBuild;
            if (Debugger.IsAttached || !isConcurrentBuild)
            {
                options.MaxDegreeOfParallelism = 1;
            }
            return options;
        }

        private static void HandleDebugSwitch(bool waitForDebugger)
        {
            if (waitForDebugger)
            {
                while (!Debugger.IsAttached)
                {
                    Thread.Sleep(3000);
                }
            }
        }
    }
}
