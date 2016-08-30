using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.TemplateEngine.Abstractions;
using Microsoft.TemplateEngine.Abstractions.Engine;
using Microsoft.TemplateEngine.Abstractions.Mount;
using Microsoft.TemplateEngine.Abstractions.Runner;
using Microsoft.TemplateEngine.Core;
using Microsoft.TemplateEngine.Core.Expressions.Cpp;
using Microsoft.TemplateEngine.Utils;
using Newtonsoft.Json.Linq;
using System.Linq;

namespace Microsoft.TemplateEngine.Orchestrator.RunnableProjects
{
    public class GlobalRunSpec : IGlobalRunSpec
    {
        public IReadOnlyList<IPathMatcher> Exclude { get; }

        public IReadOnlyList<IPathMatcher> Include { get; }

        public IReadOnlyList<IOperationProvider> Operations { get; }

        public IVariableCollection RootVariableCollection { get; }

        public IReadOnlyDictionary<IPathMatcher, IRunSpec> Special { get; }

        public IReadOnlyList<IPathMatcher> CopyOnly { get; }

        public IReadOnlyDictionary<string, string> Rename { get; }

        public bool TryGetTargetRelPath(string sourceRelPath, out string targetRelPath)
        {
            return Rename.TryGetValue(sourceRelPath, out targetRelPath);
        }

        public GlobalRunSpec(FileSource source, IDirectory templateRoot, IParameterSet parameters, IReadOnlyDictionary<string, JObject> operations, IReadOnlyDictionary<string, Dictionary<string, JObject>> special)
        {
            int expect = source.Include?.Count ?? 0;
            List<IPathMatcher> includes = new List<IPathMatcher>(expect);
            if (source.Include != null && expect > 0)
            {
                foreach (string include in source.Include)
                {
                    includes.Add(new GlobbingPatternMatcher(include));
                }
            }
            Include = includes;

            expect = source.CopyOnly?.Count ?? 0;
            List<IPathMatcher> copyOnlys = new List<IPathMatcher>(expect);
            if (source.CopyOnly != null && expect > 0)
            {
                foreach (string copyOnly in source.CopyOnly)
                {
                    copyOnlys.Add(new GlobbingPatternMatcher(copyOnly));
                }
            }
            CopyOnly = copyOnlys;

            expect = source.Exclude?.Count ?? 0;
            List<IPathMatcher> excludes = new List<IPathMatcher>(expect);
            if (source.Exclude != null && expect > 0)
            {
                foreach (string exclude in source.Exclude)
                {
                    excludes.Add(new GlobbingPatternMatcher(exclude));
                }
            }
            Exclude = excludes;

            Rename = source.Rename ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            IVariableCollection variables;
            Operations = ProcessOperations(parameters, templateRoot, operations, out variables);
            RootVariableCollection = variables;
            Dictionary<IPathMatcher, IRunSpec> specials = new Dictionary<IPathMatcher, IRunSpec>();

            if (special != null)
            {
                foreach (KeyValuePair<string, Dictionary<string, JObject>> specialEntry in special)
                {
                    IReadOnlyList<IOperationProvider> specialOps = null;
                    IVariableCollection specialVariables = variables;

                    if (specialEntry.Value != null)
                    {
                        specialOps = ProcessOperations(parameters, templateRoot, specialEntry.Value, out specialVariables);
                    }

                    RunSpec spec = new RunSpec(specialOps, specialVariables ?? variables);
                    specials[new GlobbingPatternMatcher(specialEntry.Key)] = spec;
                }
            }

            Special = specials;
        }

        private IReadOnlyList<IOperationProvider> ProcessOperations(IParameterSet parameters, IDirectory templateRoot, IReadOnlyDictionary<string, JObject> operations, out IVariableCollection variables)
        {
            List<IOperationProvider> result = new List<IOperationProvider>();
            JObject variablesSection = operations["variables"];

            JObject data;
            if (operations.TryGetValue("macros", out data))
            {
                foreach (JProperty property in data.Properties())
                {
                    RunMacros(property, variablesSection, parameters);
                }
            }

            if (operations.TryGetValue("include", out data))
            {
                string startToken = data.ToString("start");
                string endToken = data.ToString("end");
                string id = data.ToString("id");

                result.Add(new Include(startToken, endToken, x => templateRoot.FileInfo(x).OpenRead(), id));
            }

            if (operations.TryGetValue("regions", out data))
            {
                JArray regionSettings = (JArray)data["settings"];
                foreach (JToken child in regionSettings.Children())
                {
                    JObject setting = (JObject)child;
                    string id = setting.ToString("id");
                    string start = setting.ToString("start");
                    string end = setting.ToString("end");
                    bool include = setting.ToBool("include");
                    bool regionTrim = setting.ToBool("trim");
                    bool regionWholeLine = setting.ToBool("wholeLine");

                    result.Add(new Region(start, end, include, regionWholeLine, regionTrim, id));
                }
            }

            if (operations.TryGetValue("conditionals", out data))
            {
                IReadOnlyList<string> ifToken = data.ArrayAsStrings("if");
                IReadOnlyList<string> elseToken = data.ArrayAsStrings("else");
                IReadOnlyList<string> elseIfToken = data.ArrayAsStrings("elseif");
                IReadOnlyList<string> actionableIfToken = data.ArrayAsStrings("actionableIf");
                IReadOnlyList<string> actionableElseToken = data.ArrayAsStrings("actionableElse");
                IReadOnlyList<string> actionableElseIfToken = data.ArrayAsStrings("actionableElseif");
                IReadOnlyList<string> actionsToken = data.ArrayAsStrings("actions");
                IReadOnlyList<string> endIfToken = data.ArrayAsStrings("endif");
                string evaluatorName = data.ToString("evaluator");
                string id = data.ToString("id");
                bool trim = data.ToBool("trim");
                bool wholeLine = data.ToBool("wholeLine");
                ConditionEvaluator evaluator = CppStyleEvaluatorDefinition.CppStyleEvaluator;

                switch (evaluatorName)
                {
                    case "C++":
                        evaluator = CppStyleEvaluatorDefinition.CppStyleEvaluator;
                        break;
                }

                ConditionalTokens tokenVariants = new ConditionalTokens
                {
                    IfTokens = ifToken,
                    ElseTokens = elseToken,
                    ElseIfTokens = elseIfToken,
                    EndIfTokens = endIfToken,
                    ActionableElseIfTokens = actionableElseIfToken,
                    ActionableElseTokens = actionableElseToken,
                    ActionableIfTokens = actionableIfToken,
                    ActionableOperations = actionsToken
                };

                result.Add(new Conditional(tokenVariants, wholeLine, trim, evaluator, id));
            }

            if (operations.TryGetValue("flags", out data))
            {
                foreach (JProperty property in data.Properties())
                {
                    JObject innerData = (JObject)property.Value;
                    string flag = property.Name;
                    string on = innerData.ToString("on") ?? string.Empty;
                    string off = innerData.ToString("off") ?? string.Empty;
                    string onNoEmit = innerData.ToString("onNoEmit") ?? string.Empty;
                    string offNoEmit = innerData.ToString("offNoEmit") ?? string.Empty;
                    string defaultStr = innerData.ToString("default");
                    string id = innerData.ToString("id");
                    bool? @default = null;

                    if (defaultStr != null)
                    {
                        @default = bool.Parse(defaultStr);
                    }

                    result.Add(new SetFlag(flag, on, off, onNoEmit, offNoEmit, id, @default));
                }
            }

            if (operations.TryGetValue("replacements", out data))
            {
                foreach (JProperty property in data.Properties())
                {
                    if (string.IsNullOrEmpty(property.Name))
                    {
                        continue;
                    }

                    ITemplateParameter param;
                    if (parameters.TryGetParameterDefinition(property.Value.ToString(), out param))
                    {
                        Replacement r;
                        try
                        {
                            string val = parameters.ResolvedValues[param];
                            r = new Replacement(property.Name, val, null);
                        }
                        catch (KeyNotFoundException ex)
                        {
                            JObject v = property.Value as JObject;

                            if (v != null)
                            {
                                string id = v.ToString("id");
                                string replacement = v.ToString("replaceWith");
                                r = new Replacement(property.Name, replacement, id);
                            }
                            else
                            {
                                throw new Exception($"Unable to find a parameter value called \"{param.Name}\"", ex);
                            }
                        }

                        result.Add(r);
                    }
                }
            }

            variables = HandleVariables(parameters, variablesSection, result);
            return result;
        }

        private static IVariableCollection HandleVariables(IParameterSet parameters, JObject data, List<IOperationProvider> result, bool allParameters = false)
        {
            IVariableCollection vc = VariableCollection.Root();
            JToken expandToken;
            if (data.TryGetValue("expand", out expandToken) && expandToken.Type == JTokenType.Boolean && expandToken.ToObject<bool>())
            {
                result?.Add(new ExpandVariables(null));
            }

            JObject sources = (JObject)data["sources"];
            string fallbackFormat = data.ToString("fallbackFormat");
            Dictionary<string, VariableCollection> collections = new Dictionary<string, VariableCollection>();

            foreach (JProperty prop in sources.Properties())
            {
                VariableCollection c = null;
                string format = prop.Value.ToString();

                switch (prop.Name)
                {
                    case "environment":
                        c = VariableCollection.Environment(format);

                        if (fallbackFormat != null)
                        {
                            c = VariableCollection.Environment(c, fallbackFormat);
                        }
                        break;
                    case "user":
                        c = ProduceUserVariablesCollection(parameters, format, allParameters);

                        if (fallbackFormat != null)
                        {
                            VariableCollection d = ProduceUserVariablesCollection(parameters, fallbackFormat, allParameters);
                            d.Parent = c;
                            c = d;
                        }
                        break;
                }

                collections[prop.Name] = c;
            }

            foreach (JToken order in ((JArray)data["order"]).Children())
            {
                IVariableCollection current = collections[order.ToString()];

                IVariableCollection tmp = current;
                while (tmp.Parent != null)
                {
                    tmp = tmp.Parent;
                }

                tmp.Parent = vc;
                vc = current;
            }

            return vc;
        }

        private void RunMacros(JProperty macro, JObject variablesSection, IParameterSet parameters)
        {
            RunnableProjectGenerator.ParameterSet set = (RunnableProjectGenerator.ParameterSet)parameters;
            string variableName = macro.Name;
            JObject def = (JObject)macro.Value;

            switch (def["type"].ToString())
            {
                case "guid":
                    HandleGuidAction(variableName, def, set);
                    break;
                case "random":
                    HandleRandomAction(variableName, def, set);
                    break;
                case "now":
                    HandleNowAction(variableName, def, set);
                    break;
                case "evaluate":
                    HandleEvaluateAction(variableName, variablesSection, def, set);
                    break;
                case "constant":
                    HandleConstantAction(variableName, def, set);
                    break;
                case "regex":
                    HandleRegexAction(variableName, variablesSection, def, set);
                    break;
            }
        }

        private static void HandleRandomAction(string variableName, JObject def, RunnableProjectGenerator.ParameterSet parameters)
        {
            switch (def["action"].ToString())
            {
                case "new":
                    int low = def.ToInt32("low");
                    int high = def.ToInt32("high", int.MaxValue);
                    Random rnd = new Random();
                    int val = rnd.Next(low, high);
                    string value = val.ToString();
                    Parameter p = new Parameter
                    {
                        IsVariable = true,
                        Name = variableName
                    };

                    parameters.AddParameter(p);
                    parameters.ResolvedValues[p] = value;
                    break;
            }
        }

        private void HandleRegexAction(string variableName, JObject variablesSection, JObject def, RunnableProjectGenerator.ParameterSet parameters)
        {
            IVariableCollection vars = HandleVariables(parameters, variablesSection, null, true);
            string action = def.ToString("action");
            string value = null;

            switch (action)
            {
                case "replace":
                    string sourceVar = def.ToString("source");
                    JArray steps = def.Get<JArray>("steps");
                    object working;
                    if (!vars.TryGetValue(sourceVar, out working))
                    {
                        ITemplateParameter param;
                        if (!parameters.TryGetParameterDefinition(sourceVar, out param) || !parameters.ResolvedValues.TryGetValue(param, out value))
                        {
                            value = string.Empty;
                        }
                    }
                    else
                    {
                        value = working?.ToString() ?? "";
                    }

                    if (steps != null)
                    {
                        foreach (JToken child in steps)
                        {
                            JObject map = (JObject) child;
                            string regex = map.ToString("regex");
                            string replaceWith = map.ToString("replacement");

                            value = Regex.Replace(value, regex, replaceWith);
                        }
                    }
                    break;
            }

            Parameter p = new Parameter
            {
                IsVariable = true,
                Name = variableName
            };

            parameters.AddParameter(p);
            parameters.ResolvedValues[p] = value;
        }

        private static void HandleNowAction(string variableName, JObject def, RunnableProjectGenerator.ParameterSet parameters)
        {
            string format = def.ToString("action");
            bool utc = def.ToBool("utc");
            DateTime time = utc ? DateTime.UtcNow : DateTime.Now;
            string value = time.ToString(format);
            Parameter p = new Parameter
            {
                IsVariable = true,
                Name = variableName
            };

            parameters.AddParameter(p);
            parameters.ResolvedValues[p] = value;
        }

        private static void HandleConstantAction(string variableName, JObject def, RunnableProjectGenerator.ParameterSet parameters)
        {
            string value = def.ToString("action");
            Parameter p = new Parameter
            {
                IsVariable = true,
                Name = variableName
            };

            parameters.AddParameter(p);
            parameters.ResolvedValues[p] = value;
        }

        private void HandleEvaluateAction(string variableName, JObject variablesSection, JObject def, RunnableProjectGenerator.ParameterSet parameters)
        {
            ConditionEvaluator evaluator = CppStyleEvaluatorDefinition.CppStyleEvaluator;
            IVariableCollection vars = HandleVariables(parameters, variablesSection, null, true);
            switch (def.ToString("evaluator") ?? "C++")
            {
                case "C++":
                    evaluator = CppStyleEvaluatorDefinition.CppStyleEvaluator;
                    break;
            }

            byte[] data = Encoding.UTF8.GetBytes(def.ToString("action"));
            int len = data.Length;
            int pos = 0;
            IProcessorState state = new ProcessorState(vars, data, Encoding.UTF8);
            bool res = evaluator(state, ref len, ref pos);

            Parameter p = new Parameter
            {
                IsVariable = true,
                Name = variableName
            };

            parameters.AddParameter(p);
            parameters.ResolvedValues[p] = res.ToString();
        }

        private class ProcessorState : IProcessorState
        {
            public ProcessorState(IVariableCollection vars, byte[] buffer, Encoding encoding)
            {
                Config = new EngineConfig(vars);
                CurrentBuffer = buffer;
                CurrentBufferPosition = 0;
                Encoding = encoding;
                EncodingConfig = new EncodingConfig(Config, encoding);
            }

            public IEngineConfig Config { get; }

            public byte[] CurrentBuffer { get; private set; }

            public int CurrentBufferLength => CurrentBuffer.Length;

            public int CurrentBufferPosition { get; }

            public Encoding Encoding { get; set; }

            public IEncodingConfig EncodingConfig { get; }

            public bool AdvanceBuffer(int bufferPosition)
            {
                byte[] tmp = new byte[CurrentBufferLength - bufferPosition];
                Buffer.BlockCopy(CurrentBuffer, bufferPosition, tmp, 0, CurrentBufferLength - bufferPosition);
                CurrentBuffer = tmp;

                return true;
            }

            public void SeekBackUntil(ITokenTrie match)
            {
                throw new NotImplementedException();
            }

            public void SeekBackUntil(ITokenTrie match, bool consume)
            {
                throw new NotImplementedException();
            }

            public void SeekBackWhile(ITokenTrie match)
            {
                throw new NotImplementedException();
            }

            public void SeekForwardUntil(ITokenTrie trie, ref int bufferLength, ref int currentBufferPosition)
            {
                throw new NotImplementedException();
            }

            public void SeekForwardThrough(ITokenTrie trie, ref int bufferLength, ref int currentBufferPosition)
            {
                throw new NotImplementedException();
            }

            public void SeekForwardWhile(ITokenTrie trie, ref int bufferLength, ref int currentBufferPosition)
            {
                throw new NotImplementedException();
            }
        }

        private static void HandleGuidAction(string variableName, JObject def, RunnableProjectGenerator.ParameterSet parameters)
        {
            switch (def.ToString("action"))
            {
                case "new":
                    string fmt = def.ToString("format");
                    if (fmt != null)
                    {
                        Guid g = Guid.NewGuid();
                        string value = char.IsUpper(fmt[0]) ? g.ToString(fmt[0].ToString()).ToUpperInvariant() : g.ToString(fmt[0].ToString()).ToLowerInvariant();
                        Parameter p = new Parameter
                        {
                            IsVariable = true,
                            Name = variableName
                        };

                        parameters.AddParameter(p);
                        parameters.ResolvedValues[p] = value;
                    }
                    else
                    {
                        Guid g = Guid.NewGuid();
                        const string guidFormats = "ndbpxNDPBX";
                        for (int i = 0; i < guidFormats.Length; ++i)
                        {
                            Parameter p = new Parameter
                            {
                                IsVariable = true,
                                Name = variableName + "-" + guidFormats[i]
                            };

                            string rplc = char.IsUpper(guidFormats[i]) ? g.ToString(guidFormats[i].ToString()).ToUpperInvariant() : g.ToString(guidFormats[i].ToString()).ToLowerInvariant();
                            parameters.AddParameter(p);
                            parameters.ResolvedValues[p] = rplc;
                        }

                        Parameter pd = new Parameter
                        {
                            IsVariable = true,
                            Name = variableName
                        };

                        parameters.AddParameter(pd);
                        parameters.ResolvedValues[pd] = g.ToString("D");
                    }

                    break;
            }
        }

        private static VariableCollection ProduceUserVariablesCollection(IParameterSet parameters, string format, bool allParameters)
        {
            VariableCollection vc = new VariableCollection();
            foreach (ITemplateParameter parameter in parameters.ParameterDefinitions)
            {
                Parameter param = (Parameter)parameter;
                if (allParameters || param.IsVariable)
                {
                    string value;
                    string key = string.Format(format ?? "{0}", param.Name);
                    bool valueGetResult = parameters.ResolvedValues.TryGetValue(param, out value);

                    if (value == null)
                    {
                        System.Diagnostics.Debug.WriteLine("Null value for param name = {0}", param.Name);
                    }

                    if (!string.IsNullOrEmpty(param.DataType))
                    {
                        vc[key] = DataTypeSpecifiedConvertLiteral(param, value);
                    }
                    else
                    {
                        if (valueGetResult)
                        {
                            vc[key] = InferTypeAndConvertLiteral(value);
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine("Get value failed on param name = {0}", param.Name);
                        }
                    }
                }
            }

            return vc;
        }

        /// For explicitly data-typed variables, attempt to convert the variable value to the specified type.
        /// Data type names:
        ///     - choice
        ///     - bool
        ///     - float
        ///     - int
        ///     - hex
        ///     - text
        /// The data type names are case insensitive.
        /// 
        /// Returns the converted value, or throws if a conversion isn't possible.
        private static object DataTypeSpecifiedConvertLiteral(Parameter param, string literal)
        {
            if (string.Equals(param.DataType, "bool", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(literal, "true", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                else if (string.Equals(literal, "false", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                else
                {
                    // Note: if the literal is ever null, it is probably due to a problem in TemplateCreator.Instantiate()
                    // which takes care of making null bool -> true as appropriate.
                    throw new TemplateParamException(param.Name, literal, param.DataType);
                }
            }
            else if (string.Equals(param.DataType, "choice", StringComparison.OrdinalIgnoreCase))
            {
                if ((literal != null) && param.Choices.Contains(literal))
                {
                    return literal;
                }
                else
                {
                    string customBaseMessage = string.Format("Valid choices for this param are [{0}]", string.Join(",", param.Choices));
                    throw new TemplateParamException(customBaseMessage, param.Name, literal, param.DataType);
                }
            }
            else if (string.Equals(param.DataType, "float", StringComparison.OrdinalIgnoreCase))
            {
                double convertedFloat;
                if (double.TryParse(literal, out convertedFloat))
                {
                    return convertedFloat;
                }
                else
                {
                    throw new TemplateParamException(param.Name, literal, param.DataType);
                }
            }
            else if (string.Equals(param.DataType, "int", StringComparison.OrdinalIgnoreCase))
            {
                long convertedInt;
                if (long.TryParse(literal, out convertedInt))
                {
                    return convertedInt;
                }
                else
                {
                    throw new TemplateParamException(param.Name, literal, param.DataType);
                }
            }
            else if (string.Equals(param.DataType, "hex", StringComparison.OrdinalIgnoreCase))
            {
                long convertedHex;
                if (long.TryParse(literal.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out convertedHex))
                {
                    return convertedHex;
                }
                else
                {
                    throw new TemplateParamException(param.Name, literal, param.DataType);
                }
            }
            else if (string.Equals(param.DataType, "text", StringComparison.OrdinalIgnoreCase))
            {   // "text" is a valid data type, but doesn't need any special handling.
                return literal;
            }
            else
            {
                string customMessage = string.Format("Param name = [{0}] had unknown data type = [{1}]", param.Name, param.DataType);
                throw new TemplateParamException(customMessage);
            }
        }

        private static object InferTypeAndConvertLiteral(string literal)
        {
            if (literal == null)
            {
                return null;
            }

            if (!literal.Contains("\""))
            {
                if (string.Equals(literal, "true", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (string.Equals(literal, "false", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (string.Equals(literal, "null", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                double literalDouble;
                if (literal.Contains(".") && double.TryParse(literal, out literalDouble))
                {
                    return literalDouble;
                }

                long literalLong;
                if (long.TryParse(literal, out literalLong))
                {
                    return literalLong;
                }

                if (literal.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    && long.TryParse(literal.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out literalLong))
                {
                    return literalLong;
                }

                if (string.Equals("null", literal, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return literal;
            }

            return literal.Substring(1, literal.Length - 2);
        }
    }
}