﻿using System;
using System.Collections.Generic;
using System.Linq;
using CodeWriter;

namespace CodeGenerator
{
    public class EntryCodeGenerator
    {
        public Options Options { get; }
        public CodeWriter.CodeWriter CodeWriter { get; set; }

        public EntryCodeGenerator(Options options)
        {
            Options = options;

            var settings = new CodeWriterSettings(CodeWriterSettings.CSharpDefault);
            settings.TranslationMapping["`"] = "\"";
            CodeWriter = new CodeWriter.CodeWriter(settings);

            GenerateHead();
        }

        private void GenerateHead()
        {
            CodeWriter.HeadLines = new List<string>()
            {
                "// ------------------------------------------------------------------------------",
                "// <auto-generated>",
                "//     This code was generated by EuNet.CodeGenerator.",
                "//",
                "//     Changes to this file may cause incorrect behavior and will be lost if",
                "//     the code is regenerated.",
                "// </auto-generated>",
                "// ------------------------------------------------------------------------------",
                "",
                "using System;",
                "using System.Collections.Generic;",
                "using System.Reflection;",
                "using System.Threading.Tasks;",
                "using EuNet.Core;",
                "using EuNet.Rpc;",
                "#if UNITY_EDITOR || UNITY_STANDALONE || UNITY_ANDROID || UNITY_IOS",
                "using EuNet.Unity;",
                "using UnityEngine;",
                "#endif"
            };
        }

        public void GenerateCode(Type[] types)
        {
            HashSet<string> namespaceHashSet = new HashSet<string>();
            Dictionary<int, string> rpcEnumMap = new Dictionary<int, string>();

            var rpcTypes = types.Where(t => Utility.IsRpcInterface(t)).ToArray();
            var rpcCodeGen = new RpcCodeGenerator() { Options = Options };
            foreach (var type in rpcTypes)
                rpcCodeGen.GenerateCode(type, rpcEnumMap, CodeWriter);

            var netViewRpcTypes = types.Where(t => Utility.IsViewRpcInterface(t)).ToArray();
            var netViewRpcCodeGen = new ViewRpcCodeGenerator() { Options = Options };
            foreach (var type in netViewRpcTypes)
                netViewRpcCodeGen.GenerateCode(type, rpcEnumMap, CodeWriter);

            var rpcEnumCodeGen = new RpcEnumCodeGenerator() { Options = Options };
            rpcEnumCodeGen.GenerateCode(rpcEnumMap, CodeWriter);

            var aotCodeGen = new AotCodeGenerator() { Options = Options };
            aotCodeGen.GenerateCode(rpcTypes.Concat(netViewRpcTypes).ToArray(), CodeWriter);

            Dictionary<string, string> formatterMap = new Dictionary<string, string>();

            var netDataObjectTypes = types.Where(t => Utility.IsNetDataObjectAttribute(t)).ToArray();
            var formatterCodeGen = new FormatterCodeGenerator() { Options = Options };
            foreach (var type in netDataObjectTypes)
            {
                formatterCodeGen.GenerateCode(type, formatterMap, CodeWriter);

                /*if(string.IsNullOrEmpty(type.Namespace) == false &&
                    namespaceHashSet.Contains(type.Namespace) == false)
                {
                    namespaceHashSet.Add(type.Namespace);
                    CodeWriter.HeadLines.Add($"using {type.Namespace};");
                }*/
            }

            CodeWriter.HeadLines.Add("");

            var resolverCodeGen = new ResolverCodeGenerator() { Options = Options };
            resolverCodeGen.GenerateCode(formatterMap, CodeWriter);
        }
    }
}
