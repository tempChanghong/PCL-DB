<#
.SYNOPSIS
Compare a preserved Beta 8 VB Core with migrated Core in separate .NET Framework 4.8 processes.
.DESCRIPTION
Keeps raw reflection evidence and compares callable declarations after narrowly documented
VB/C# compiler-emission normalization. Resources are compared by name and SHA-256 bytes;
representative configuration/JSON values are compared without object-property ordering noise.
RunBaselineTests copies the unchanged, previously compiled VB test runtime, substitutes only
Core, verifies EXE/DLL hashes, and requires all 7305 original checks. No application build,
WPF UI, external launcher configuration writes, packaging or publishing is performed.
The result records exact missing/added declarations and every permitted compiler exception;
it does not claim raw CLR metadata or method bodies are byte-for-byte equal.
#>
param(
    [Parameter(Mandatory)][string]$BaselineCorePath,
    [Parameter(Mandatory)][string]$CandidateCorePath,
    [string]$BaselineTestsDirectory,
    [string]$DependencyDirectory,
    [string]$ReportDirectory,
    [switch]$RunBaselineTests
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $PSScriptRoot 'VerificationProcessTools.psm1') -Force
$baseline = (Resolve-Path -LiteralPath $BaselineCorePath).Path
$candidate = (Resolve-Path -LiteralPath $CandidateCorePath).Path
if (!$DependencyDirectory) { $DependencyDirectory = $BaselineTestsDirectory }
if (!$DependencyDirectory) { $DependencyDirectory = Split-Path $baseline -Parent }
$dependencies = (Resolve-Path -LiteralPath $DependencyDirectory).Path
$json = Join-Path $dependencies 'Newtonsoft.Json.dll'
if (!(Test-Path -LiteralPath $json -PathType Leaf)) { throw 'Newtonsoft.Json.dll must be supplied in DependencyDirectory.' }
if (!$ReportDirectory) { $ReportDirectory = Join-Path $root 'artifacts/core-csharp-migration/contracts' }
$operation = New-PclDbVerificationDirectory -Root $ReportDirectory -Scope 'core-contract'
$resultPath = Join-Path $operation 'result.json'
$sourcePath = Join-Path $operation 'CoreContractProbe.cs'
$probe = Join-Path $operation 'CoreContractProbe.exe'
$result = [ordered]@{Format='PCL-DB-core-migration/v2';Succeeded=$false;Operation=$operation;
    BaselineCore=$baseline;CandidateCore=$candidate;SameInputFile=$baseline.Equals($candidate,[StringComparison]::OrdinalIgnoreCase);
    BaselineCoreSHA256=(Get-FileHash -LiteralPath $baseline).Hash;CandidateCoreSHA256=(Get-FileHash -LiteralPath $candidate).Hash;
    GeneratedAtUtc=[DateTime]::UtcNow.ToString('o');BaselineSnapshot=$null;CandidateSnapshot=$null;Differences=$null;BinaryCompatibility=$null;
    Limits=@('Public and friend-accessible declarations are compared; method bodies are not proved equivalent.',
        'Raw CLR metadata may differ only under the explicit CompilerEmission rules and isolated NativeIsolation interface bridge check; RawDifferences remain available.',
        'JSON/configuration fixtures are representative, not an exhaustive schema inventory.',
        'Binary compatibility means executing the unchanged baseline VB test host against the candidate DLL; tests use isolated fixtures.')}

@'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
[assembly:System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName=".NET Framework 4.8")]
internal static class CoreContractProbe {
    const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
    static string dependencyDirectory;
    static string targetDirectory;
    static string TypeName(Type t) {
        if (t.IsByRef) return TypeName(t.GetElementType()) + "&";
        if (t.IsPointer) return TypeName(t.GetElementType()) + "*";
        if (t.IsArray) return TypeName(t.GetElementType()) + "[" + new string(',', t.GetArrayRank()-1) + "]";
        if (t.IsGenericParameter) return (t.DeclaringMethod == null ? "!" : "!!") + t.GenericParameterPosition + ":" + t.Name;
        if (!t.IsGenericType) return t.FullName ?? t.Name;
        return (t.GetGenericTypeDefinition().FullName ?? t.Name) + "<" + string.Join(",", t.GetGenericArguments().Select(TypeName)) + ">";
    }
    static string Value(object value) {
        if (value == null) return "null";
        if (value == DBNull.Value) return "DBNull";
        if (value == Missing.Value) return "Missing";
        var type = value as Type;
        if (type != null) return "type:" + TypeName(type);
        if (value is string) return "string:" + JsonConvert.SerializeObject(value);
        if (value is char) return "char:" + ((int)(char)value).ToString(CultureInfo.InvariantCulture);
        return TypeName(value.GetType()) + ":" + Convert.ToString(value, CultureInfo.InvariantCulture);
    }
    static bool IgnoredAttribute(Type type) {
        var name = type.FullName;
        return name == "System.Runtime.CompilerServices.CompilerGeneratedAttribute" ||
            name == "System.Runtime.CompilerServices.AsyncStateMachineAttribute" ||
            name == "System.Runtime.CompilerServices.IteratorStateMachineAttribute" ||
            (name != null && name.StartsWith("System.Diagnostics.Debugger", StringComparison.Ordinal));
    }
    static string AttributeArgument(CustomAttributeTypedArgument arg) {
        var array = arg.Value as IEnumerable<CustomAttributeTypedArgument>;
        return TypeName(arg.ArgumentType) + "=" + (array != null ? "[" + string.Join(",", array.Select(AttributeArgument)) + "]" : Value(arg.Value));
    }
    static string Attributes(IEnumerable<CustomAttributeData> attributes) {
        return string.Join(";", attributes.Where(a => !IgnoredAttribute(a.AttributeType)).Select(a =>
            TypeName(a.AttributeType) + "(" + string.Join(",", a.ConstructorArguments.Select(AttributeArgument)) + "){" +
            string.Join(",", a.NamedArguments.OrderBy(n=>n.MemberName,StringComparer.Ordinal).Select(n=>n.MemberName+"="+AttributeArgument(n.TypedValue))) + "}")
            .OrderBy(x=>x,StringComparer.Ordinal));
    }
    static string Generic(Type[] args) {
        return string.Join(";", args.Select(a=>TypeName(a)+" flags="+((int)a.GenericParameterAttributes)+" constraints="+
            string.Join(",", a.GetGenericParameterConstraints().Select(TypeName).OrderBy(x=>x,StringComparer.Ordinal))));
    }
    static string Parameter(ParameterInfo p) { return Parameter(p, p.Name); }
    static string Parameter(ParameterInfo p, string parameterName) {
        return p.Position + ":" + parameterName + ":" + TypeName(p.ParameterType) + " flags=" + ((int)p.Attributes) +
            " default=" + (p.HasDefaultValue ? Value(p.RawDefaultValue) : "<none>") +
            " required="+string.Join(",",p.GetRequiredCustomModifiers().Select(TypeName))+
            " optional="+string.Join(",",p.GetOptionalCustomModifiers().Select(TypeName))+
            " attrs=" + Attributes(p.GetCustomAttributesData());
    }
    static bool Accessible(MethodBase m, bool friends) { return friends ? !m.IsPrivate : m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly; }
    static bool Accessible(FieldInfo f, bool friends) { return friends ? !f.IsPrivate : f.IsPublic || f.IsFamily || f.IsFamilyOrAssembly; }
    static bool MyTemplate(Type t) {
        if (t.IsVisible) return false;
        for (var owner=t;owner!=null;owner=owner.DeclaringType)
            if(owner.GetCustomAttributesData().Any(a=>a.AttributeType.FullName=="System.CodeDom.Compiler.GeneratedCodeAttribute" && a.ConstructorArguments.Count>0 && (string)a.ConstructorArguments[0].Value=="MyTemplate")) return true;
        return false;
    }
    static bool TypeAccessible(Type t, bool friends, bool raw) {
        if (t.IsDefined(typeof(CompilerGeneratedAttribute),false) || t.Name.Contains("$")) return false;
        if (!raw && MyTemplate(t)) return false;
        if (!friends) return t.IsVisible;
        return t.IsNested ? !t.IsNestedPrivate && TypeAccessible(t.DeclaringType,true,raw) : true;
    }
    static readonly Dictionary<Type,JObject> IsolationBridges=new Dictionary<Type,JObject>();
    static JObject IsolationBridge(Type t) {
        JObject cached;if(IsolationBridges.TryGetValue(t,out cached))return cached;
        var result=new JObject{{"Succeeded",false}};IsolationBridges[t]=result;
        if(t.FullName!="PclDownloadBridge.Core.NativeInstanceIsolation" || !t.IsSealed || t.IsVisible || t.IsAbstract || t.BaseType!=typeof(object))return result;
        var getter=t.GetMethod("get_Isolated",Members);
        var contract=t.Assembly.GetType("PclDownloadBridge.Core.IInstanceIsolationPlan",true);
        if(getter==null || !getter.IsAssembly || getter.IsStatic || !getter.IsSpecialName || getter.ReturnType!=typeof(bool) || getter.GetParameters().Length!=0)return result;
        var map=t.GetInterfaceMap(contract);var index=Array.FindIndex(map.InterfaceMethods,m=>m.Name=="get_Isolated");
        if(index<0)return result;var target=map.TargetMethods[index];
        var body=getter.GetMethodBody().GetILAsByteArray();
        if(body.Length!=7 || body[0]!=0x02 || body[1]!=0x7b || body[6]!=0x2a)return result;
        var field=t.Module.ResolveField(BitConverter.ToInt32(body,2));
        if(field.DeclaringType!=t || !field.IsPrivate || field.IsStatic || field.FieldType!=typeof(bool))return result;
        if(target!=getter) {
            if(!target.IsPrivate || !target.IsFinal || !target.IsVirtual || target.ReturnType!=typeof(bool) || target.GetParameters().Length!=0)return result;
            var bridge=target.GetMethodBody().GetILAsByteArray();
            if(bridge.Length!=7 || bridge[0]!=0x02 || bridge[1]!=0x28 || bridge[6]!=0x2a || t.Module.ResolveMethod(BitConverter.ToInt32(bridge,2))!=getter)return result;
        }
        var instance=System.Runtime.Serialization.FormatterServices.GetUninitializedObject(t);
        foreach(var value in new[]{false,true}) {
            field.SetValue(instance,value);
            if((bool)getter.Invoke(instance,null)!=value || (bool)map.InterfaceMethods[index].Invoke(instance,null)!=value)return result;
        }
        result["Succeeded"]=true;result["Type"]=TypeName(t);result["GetterFlags"]=(int)getter.Attributes;
        result["Target"]=target.Name;result["TargetFlags"]=(int)target.Attributes;result["DirectGetterFieldRead"]=true;
        result["InterfaceReturnsSamePropertyForFalseAndTrue"]=true;result["ConstructorWasNotInvoked"]=true;
        return result;
    }
    static string Method(MethodBase m, bool raw) {
        var method = m as MethodInfo;
        var flags=m.Attributes;
        if(!raw) {
            flags &= ~MethodAttributes.HideBySig;
            // VB emits this extra override-access check on accessible virtual members.
            // Access, virtual, abstract, final and slot flags remain strictly compared.
            if(m.IsPublic || m.IsFamily || m.IsFamilyOrAssembly) flags &= ~MethodAttributes.CheckAccessOnOverride;
            if(m.Name=="get_Isolated" && m.DeclaringType.FullName=="PclDownloadBridge.Core.NativeInstanceIsolation" &&
                ((int)flags==2915 || (int)flags==2051) && (bool)IsolationBridge(m.DeclaringType)["Succeeded"])
                flags &= ~(MethodAttributes.Virtual|MethodAttributes.Final|MethodAttributes.NewSlot|MethodAttributes.CheckAccessOnOverride);
        }
        var parameters=m.GetParameters();
        Func<ParameterInfo,string> parameter=p=>{
            var name=p.Name;
            if(!raw && m.IsSpecialName && m.Name.StartsWith("set_",StringComparison.Ordinal) &&
                m.IsDefined(typeof(CompilerGeneratedAttribute),false) && p.Position==parameters.Length-1 &&
                (name=="value" || name=="AutoPropertyValue")) name="value";
            if(!raw && m.DeclaringType.BaseType==typeof(MulticastDelegate)) {
                if(m.IsConstructor) name=p.Position==0?"delegateTarget":"delegateMethod";
                if(m.Name=="BeginInvoke" && p.Position>=parameters.Length-2) name=p.Position==parameters.Length-2?"delegateCallback":"delegateState";
                if(m.Name=="EndInvoke" && p.Position==parameters.Length-1) name="delegateResult";
            }
            return Parameter(p,name);
        };
        return m.MemberType+" "+m.Name+" flags="+((int)flags)+" impl="+((int)m.MethodImplementationFlags)+" calling="+((int)m.CallingConvention)+
            " result="+(method==null ? "<ctor>" : TypeName(method.ReturnType)+" attrs="+Attributes(method.ReturnParameter.GetCustomAttributesData()))+
            " generic="+(m.IsGenericMethodDefinition ? Generic(m.GetGenericArguments()) : "")+
            " params=("+string.Join("|",parameters.Select(parameter))+") attrs="+Attributes(m.GetCustomAttributesData());
    }
    static List<string> Api(Assembly a, bool friends, bool raw) {
        var lines = new List<string>();
        foreach (var t in a.GetTypes().Where(t=>TypeAccessible(t,friends,raw)).OrderBy(TypeName,StringComparer.Ordinal)) {
            string prefix=TypeName(t)+" :: ";
            var flags=t.Attributes;
            // BeforeFieldInit has no initializer timing to change when no cctor exists.
            // Types with a real initializer keep the exact flag and static fields below.
            if(!raw && t.TypeInitializer==null) flags &= ~TypeAttributes.BeforeFieldInit;
            lines.Add(prefix+"TYPE flags="+((int)flags)+" base="+(t.BaseType==null?"<none>":TypeName(t.BaseType))+
                " interfaces="+string.Join(",",t.GetInterfaces().Select(TypeName).OrderBy(x=>x,StringComparer.Ordinal))+
                " generic="+(t.IsGenericTypeDefinition ? Generic(t.GetGenericArguments()):"")+" attrs="+Attributes(t.GetCustomAttributesData()));
            if (t.IsEnum) lines.Add(prefix+"ENUM underlying="+TypeName(Enum.GetUnderlyingType(t)));
            foreach(var m in t.GetConstructors(Members).Where(m=>Accessible(m,friends))) lines.Add(prefix+Method(m,raw));
            foreach(var m in t.GetMethods(Members).Where(m=>Accessible(m,friends))) lines.Add(prefix+Method(m,raw));
            foreach(var f in t.GetFields(Members).Where(f=>Accessible(f,friends)))
                lines.Add(prefix+"FIELD "+f.Name+":"+TypeName(f.FieldType)+" flags="+((int)f.Attributes)+
                    " constant="+(f.IsLiteral?Value(f.GetRawConstantValue()):"<none>")+" attrs="+Attributes(f.GetCustomAttributesData()));
            foreach(var p in t.GetProperties(Members)) {
                var getter=p.GetGetMethod(true);var setter=p.GetSetMethod(true);
                if ((getter==null || !Accessible(getter,friends)) && (setter==null || !Accessible(setter,friends))) continue;
                if(!raw) { if(getter!=null && !Accessible(getter,friends)) getter=null; if(setter!=null && !Accessible(setter,friends)) setter=null; }
                lines.Add(prefix+"PROPERTY "+p.Name+":"+TypeName(p.PropertyType)+" flags="+((int)p.Attributes)+
                    " getter="+(getter==null?"<none>":Method(getter,raw))+" setter="+(setter==null?"<none>":Method(setter,raw))+
                    " indexes="+string.Join("|",p.GetIndexParameters().Select(Parameter))+" attrs="+Attributes(p.GetCustomAttributesData()));
            }
            foreach(var e in t.GetEvents(Members)) {
                var add=e.GetAddMethod(true);var remove=e.GetRemoveMethod(true);
                if ((add==null || !Accessible(add,friends)) && (remove==null || !Accessible(remove,friends))) continue;
                if(!raw) { if(add!=null && !Accessible(add,friends)) add=null; if(remove!=null && !Accessible(remove,friends)) remove=null; }
                lines.Add(prefix+"EVENT "+e.Name+":"+TypeName(e.EventHandlerType)+" flags="+((int)e.Attributes)+
                    " add="+(add==null?"<none>":Method(add,raw))+" remove="+(remove==null?"<none>":Method(remove,raw))+" attrs="+Attributes(e.GetCustomAttributesData()));
            }
        }
        lines.Sort(StringComparer.Ordinal);return lines;
    }
    static JObject Initialization(Assembly a) {
        var result=new JObject();
        foreach(var t in a.GetTypes().Where(t=>TypeAccessible(t,true,false)).OrderBy(TypeName,StringComparer.Ordinal)) {
            var fields=t.GetFields(Members).Where(f=>f.IsStatic && !f.IsLiteral).OrderBy(f=>f.Name,StringComparer.Ordinal).ToArray();
            if(t.TypeInitializer==null && fields.Length==0)continue;
            result[TypeName(t)]=new JObject{{"BeforeFieldInit",(t.Attributes&TypeAttributes.BeforeFieldInit)!=0},{"HasInitializer",t.TypeInitializer!=null},
                {"Fields",new JArray(fields.Select(f=>f.Name+":"+TypeName(f.FieldType)+" flags="+(int)f.Attributes))}};
        }
        return result;
    }
    static JObject CompilerDifferences(Assembly a) {
        var types=a.GetTypes().Where(t=>TypeAccessible(t,true,true)).OrderBy(TypeName,StringComparer.Ordinal).ToArray();
        return new JObject {
            {"NativeIsolationInterfaceBridge",IsolationBridge(a.GetType("PclDownloadBridge.Core.NativeInstanceIsolation",true))},
            {"ExcludedMyTemplateTypes",new JArray(types.Where(MyTemplate).Select(t=>TypeName(t)+" attrs="+Attributes(t.GetCustomAttributesData())))},
            {"BeforeFieldInitWithoutInitializer",new JArray(types.Where(t=>t.TypeInitializer==null&&(t.Attributes&TypeAttributes.BeforeFieldInit)!=0).Select(TypeName))},
            {"MethodFlagNormalization",new JArray(types.Where(t=>!MyTemplate(t)).SelectMany(t=>t.GetConstructors(Members).Cast<MethodBase>().Concat(t.GetMethods(Members)))
                .Where(m=>Accessible(m,true)&&((m.Attributes&MethodAttributes.HideBySig)!=0||((m.IsPublic||m.IsFamily||m.IsFamilyOrAssembly)&&(m.Attributes&MethodAttributes.CheckAccessOnOverride)!=0)))
                .Select(m=>TypeName(m.DeclaringType)+" :: "+m.Name+" flags="+(int)m.Attributes+" visibility="+(m.IsPublic?"public":m.IsFamily?"protected":m.IsFamilyOrAssembly?"protected-internal":m.IsAssembly?"internal":"other")))},
            {"Rules",new JArray("Ignore HideBySig bit only; preserve all visibility, static, virtual, abstract, final and slot flags.",
                "Ignore CheckAccessOnOverride bit only for public/protected/protected-internal methods; retain full raw declarations.",
                "Normalize only CompilerGenerated setter final parameter named AutoPropertyValue/value and runtime delegate generated parameters; preserve user parameter names/defaults.",
                "Exclude private property/event accessors from callable contracts; retain them in raw API evidence.",
                "Ignore BeforeFieldInit only on types without a cctor; compare all real initializer flags and fields.",
                "Exclude non-public GeneratedCode(MyTemplate) trees, which are compiler helpers and may be friend-accessible; retain explicit excluded list.",
                "Only NativeInstanceIsolation.get_Isolated permits VB implicit final virtual/internal getter versus C# internal getter/private interface bridge, after exact getter-field and bridge IL plus interface dispatch false/true checks; no constructor or external config is invoked.")}
        };
    }
    static string Sha(Stream stream) { using(var sha=SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-",""); }
    static JToken Canonical(JToken token) {
        var obj=token as JObject;if(obj!=null)return new JObject(obj.Properties().OrderBy(p=>p.Name,StringComparer.Ordinal).Select(p=>new JProperty(p.Name,Canonical(p.Value))));
        var arr=token as JArray;if(arr!=null)return new JArray(arr.Select(Canonical));return token.DeepClone();
    }
    static JObject Fixtures(Assembly a) {
        var result=new JObject();
        Action<string,string,string,string> sample=(key,typeName,input,method)=>{
            var type=a.GetType("PclDownloadBridge.Core."+typeName,true);
            object value=JsonConvert.DeserializeObject(input,type);
            if(method!=null)value=type.GetMethod(method,BindingFlags.Public|BindingFlags.Instance).Invoke(value,null);
            result[key]=Canonical(value is JToken ? (JToken)value : JToken.FromObject(value));
        };
        sample("download-default","DownloadFile","{}",null);
        sample("download-fields","DownloadFile","{\"RelativePath\":\"libraries/example.jar\",\"Url\":\"https://example.invalid/a\",\"Size\":-1,\"EstimatedSize\":0,\"AlternateUrls\":[\"https://mirror.invalid/a\"],\"Sha1\":\"abc\",\"Category\":\"libraries\"}",null);
        sample("progress","TransferProgress","{\"PhaseFractions\":{\"assets\":0.25,\"libraries\":1},\"Phase\":\"processors\",\"Total\":20,\"Completed\":2,\"NetworkBytes\":4294967296,\"BytesPerSecond\":1.5}",null);
        sample("install-result","InstallResult","{\"InstancePath\":\"X:\\\\fixture\\\\versions\\\\test\",\"FileCount\":3,\"Reused\":1,\"NetworkBytes\":4294967296}",null);
        sample("version","VersionEntry","{\"Id\":\"1.20.1\",\"Kind\":\"release\",\"Url\":\"https://example.invalid/version\",\"Released\":\"2024-01-02T03:04:05Z\"}",null);
        sample("component-default","ComponentEntry","{}",null);
        sample("component-api","ComponentEntry","{\"Kind\":3,\"Version\":\"1.0\",\"Minecraft\":\"1.20.1\",\"Data\":{\"name\":\"API fixture\"}}",null);
        sample("query-default","ModpackQuery","{}",null);
        sample("query-values","ModpackQuery","{\"Kind\":0,\"Loader\":\"fabric\",\"Text\":\"query\",\"Minecraft\":\"1.20.1\",\"Category\":\"/\",\"Platforms\":3}",null);
        sample("community-default-config","CommunityResourcePreferences","{}","ToJson");
        sample("community-config","CommunityResourcePreferences","{\"Source\":1,\"FileNameStyle\":4,\"LocalModStyle\":1}","ToJson");
        sample("launcher-pcl","ExternalLauncherBinding","{\"Id\":\"pcl-fixture\",\"Kind\":0,\"Workspace\":\"X:\\\\fixture\",\"ConfigPath\":null,\"SharedConfigPath\":null}","ToJson");
        sample("launcher-hmcl","ExternalLauncherBinding","{\"Id\":\"hmcl-fixture\",\"Kind\":1,\"Workspace\":\"X:\\\\fixture\",\"ConfigPath\":\"X:\\\\fixture\\\\hmcl.json\",\"SharedConfigPath\":null}","ToJson");
        var pref=a.GetType("PclDownloadBridge.Core.CommunityResourcePreferences",true);
        foreach(var pair in new[]{new[]{"invalid-config","{\"source\":true,\"fileNameStyle\":100,\"localModStyle\":-1}"},new[]{"valid-config","{\"source\":0,\"fileNameStyle\":2,\"localModStyle\":1}"}}){
            var value=pref.GetMethod("Read",BindingFlags.Public|BindingFlags.Static).Invoke(null,new object[]{JToken.Parse(pair[1])});
            result[pair[0]]=Canonical((JToken)pref.GetMethod("ToJson").Invoke(value,null));
        }
        return result;
    }
    static Assembly Resolve(object sender,ResolveEventArgs e) {
        var name=new AssemblyName(e.Name).Name+".dll";
        foreach(var dir in new[]{targetDirectory,dependencyDirectory}){var path=Path.Combine(dir,name);if(File.Exists(path))return Assembly.LoadFrom(path);}return null;
    }
    static int Main(string[] args) {
        try {
            CultureInfo.CurrentCulture=CultureInfo.InvariantCulture;CultureInfo.CurrentUICulture=CultureInfo.InvariantCulture;
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling",false);AppContext.SetSwitch("Switch.System.IO.BlockLongPaths",false);
            var path=Path.GetFullPath(args[0]);targetDirectory=Path.GetDirectoryName(path);dependencyDirectory=Path.GetFullPath(args[2]);
            AppDomain.CurrentDomain.AssemblyResolve+=Resolve;
            var a=Assembly.LoadFrom(path);var name=a.GetName();var file=FileVersionInfo.GetVersionInfo(path);
            var resources=new JObject();foreach(var resource in a.GetManifestResourceNames().OrderBy(x=>x,StringComparer.Ordinal))using(var stream=a.GetManifestResourceStream(resource))resources[resource]=Sha(stream);
            var identity=new JObject{{"Name",name.Name},{"Version",name.Version.ToString()},{"Culture",name.CultureName??""},{"PublicKeyToken",BitConverter.ToString(name.GetPublicKeyToken()??new byte[0]).Replace("-","")},
                {"FileVersion",file.FileVersion},{"TargetFramework",Attributes(a.GetCustomAttributesData().Where(x=>x.AttributeType.FullName=="System.Runtime.Versioning.TargetFrameworkAttribute"))},
                {"Friends",new JArray(a.GetCustomAttributesData().Where(x=>x.AttributeType.FullName=="System.Runtime.CompilerServices.InternalsVisibleToAttribute").Select(x=>(string)x.ConstructorArguments[0].Value).OrderBy(x=>x,StringComparer.Ordinal))}};
            var data=new JObject{{"Identity",identity},{"PublicApi",new JArray(Api(a,false,true))},{"FriendApi",new JArray(Api(a,true,true))},
                {"ContractPublicApi",new JArray(Api(a,false,false))},{"ContractFriendApi",new JArray(Api(a,true,false))},
                {"StaticInitialization",Initialization(a)},{"CompilerEmission",CompilerDifferences(a)},{"Resources",resources},{"JsonFixtures",Fixtures(a)},
                {"Diagnostics",new JObject{{"AssemblyLocation",a.Location},{"ProductVersion",file.ProductVersion},{"ImageRuntimeVersion",a.ImageRuntimeVersion},{"ReferencedAssemblies",new JArray(a.GetReferencedAssemblies().Select(x=>x.FullName).OrderBy(x=>x,StringComparer.Ordinal))}}}};
            File.WriteAllText(args[1],data.ToString(Formatting.Indented),new UTF8Encoding(false));return 0;
        } catch(Exception e) {Console.Error.WriteLine(e);return 1;}
    }
}
'@ | Set-Content -LiteralPath $sourcePath -Encoding UTF8

try {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    if (!(Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework/v4.0.30319/csc.exe' }
    & $compiler /nologo /target:exe ('/win32manifest:' + (Join-Path $root 'src/PclDownloadBridge.App/app.manifest')) ('/reference:'+$json) ('/out:'+$probe) $sourcePath *> (Join-Path $operation 'compile.log')
    if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath (Join-Path $operation 'compile.log'); throw 'Contract probe compilation failed.' }
    Copy-Item -LiteralPath $json -Destination (Join-Path $operation 'Newtonsoft.Json.dll')
    foreach ($item in @(@('baseline',$baseline),@('candidate',$candidate))) {
        $snapshot = Join-Path $operation ($item[0]+'.json')
        $null = Invoke-PclDbVerificationProcess -Name ($item[0]+' .NET48 Core contract') -FilePath $probe -Arguments ('"'+$item[1]+'" "'+$snapshot+'" "'+$dependencies+'"') -WorkingDirectory $operation -StdoutPath ($snapshot+'.stdout.log') -StderrPath ($snapshot+'.stderr.log') -ExpectedOutputPaths @($snapshot) -TimeoutSeconds 120
        $result[$item[0]+'Snapshot']=$snapshot
    }
    $left=Get-Content -LiteralPath $result.BaselineSnapshot -Raw -Encoding UTF8|ConvertFrom-Json
    $right=Get-Content -LiteralPath $result.CandidateSnapshot -Raw -Encoding UTF8|ConvertFrom-Json
    function Compare-Lines($Left,$Right) {
        $l=@($Left);$r=@($Right)
        if ($l.Count -eq 0 -or $r.Count -eq 0) { return [ordered]@{Missing=$l;Added=$r} }
        $diff=@(Compare-Object -ReferenceObject $l -DifferenceObject $r -CaseSensitive)
        [ordered]@{Missing=@($diff|Where-Object SideIndicator -CEQ '<='|ForEach-Object InputObject);Added=@($diff|Where-Object SideIndicator -CEQ '=>'|ForEach-Object InputObject)}
    }
    function Object-Lines($Value) { @($Value.PSObject.Properties|Sort-Object Name|ForEach-Object{$_.Name+'='+($_.Value|ConvertTo-Json -Depth 20 -Compress)}) }
    $diffs=[ordered]@{Identity=(Compare-Lines (Object-Lines $left.Identity) (Object-Lines $right.Identity));
        PublicApi=(Compare-Lines $left.ContractPublicApi $right.ContractPublicApi);FriendApi=(Compare-Lines $left.ContractFriendApi $right.ContractFriendApi);
        StaticInitialization=(Compare-Lines (Object-Lines $left.StaticInitialization) (Object-Lines $right.StaticInitialization));
        Resources=(Compare-Lines (Object-Lines $left.Resources) (Object-Lines $right.Resources));JsonFixtures=(Compare-Lines (Object-Lines $left.JsonFixtures) (Object-Lines $right.JsonFixtures))}
    $result.Differences=$diffs
    $result['RawDifferences']=[ordered]@{PublicApi=(Compare-Lines $left.PublicApi $right.PublicApi);FriendApi=(Compare-Lines $left.FriendApi $right.FriendApi)}
    $result['CompilerEmission']=[ordered]@{Baseline=$left.CompilerEmission;Candidate=$right.CompilerEmission}
    $contractPassed=@($diffs.Values|Where-Object{$_.Missing.Count -ne 0 -or $_.Added.Count -ne 0}).Count -eq 0
    $result['ContractPassed']=$contractPassed
    $result['PublicApiEntries']=@{Baseline=@($left.ContractPublicApi).Count;Candidate=@($right.ContractPublicApi).Count}
    $result['FriendApiEntries']=@{Baseline=@($left.ContractFriendApi).Count;Candidate=@($right.ContractFriendApi).Count}
    $result['ResourceCount']=@($left.Resources.PSObject.Properties).Count
    $result['JsonFixtureCount']=@($left.JsonFixtures.PSObject.Properties).Count
    if ($RunBaselineTests) {
        if (!$BaselineTestsDirectory) { throw 'RunBaselineTests requires BaselineTestsDirectory.' }
        $tests=(Resolve-Path -LiteralPath $BaselineTestsDirectory).Path
        $oldHost=Join-Path $tests 'PclDownloadBridge.Tests.exe'
        if (!(Test-Path -LiteralPath $oldHost -PathType Leaf)) { throw 'The unchanged baseline VB test executable is missing.' }
        $runtime=Join-Path $operation 'unchanged-vb-test-runtime';New-Item -ItemType Directory -Path $runtime|Out-Null
        Get-ChildItem -LiteralPath $tests -File|ForEach-Object{Copy-Item -LiteralPath $_.FullName -Destination $runtime}
        Copy-Item -LiteralPath $candidate -Destination (Join-Path $runtime 'PclDownloadBridge.Core.dll') -Force
        $hostPath=Join-Path $runtime 'PclDownloadBridge.Tests.exe'
        if ((Get-FileHash -LiteralPath $hostPath).Hash -cne (Get-FileHash -LiteralPath $oldHost).Hash -or
            (Get-FileHash -LiteralPath (Join-Path $runtime 'PclDownloadBridge.Core.dll')).Hash -cne $result.CandidateCoreSHA256) { throw 'Binary substitution identity mismatch.' }
        $run=Invoke-PclDbVerificationProcess -Name 'unchanged Beta8 VB tests with migrated Core' -FilePath $hostPath -WorkingDirectory $runtime -StdoutPath (Join-Path $operation 'old-host.stdout.log') -StderrPath (Join-Path $operation 'old-host.stderr.log') -TimeoutSeconds 900
        $summary=[regex]::Matches([IO.File]::ReadAllText((Join-Path $operation 'old-host.stdout.log')),'(?m)^ALL PASSED: (\d+) checks\.')
        if ($summary.Count -ne 1 -or [int]$summary[0].Groups[1].Value -ne 7305) { throw 'Unchanged baseline test host did not complete all 7305 checks.' }
        $result.BinaryCompatibility=[ordered]@{Succeeded=$true;Count=7305;TestHostSHA256=(Get-FileHash -LiteralPath $oldHost).Hash;
            CandidateCoreSHA256=$result.CandidateCoreSHA256;OriginalTestHostBytesPreserved=$true;DurationMilliseconds=$run.DurationMilliseconds;
            Stdout=$run.Stdout;Stderr=$run.Stderr}
    }
    $result.Succeeded=$contractPassed
    if (!$contractPassed) { throw 'Core migration contract differences detected. Inspect result.json for exact missing/added declarations, resources and JSON fixtures.' }
    Write-Output ('PASS Core contract: '+$result.PublicApiEntries.Baseline+' public, '+$result.FriendApiEntries.Baseline+' friend-accessible entries; '+$result.ResourceCount+' resources; '+$result.JsonFixtureCount+' JSON/config fixtures.')
} catch {
    $result['Error']=$_.Exception.ToString()
    throw
} finally {
    $result['CompletedAtUtc']=[DateTime]::UtcNow.ToString('o')
    $result|ConvertTo-Json -Depth 22|Set-Content -LiteralPath $resultPath -Encoding UTF8
    Write-Output ('Report: '+$resultPath)
}
