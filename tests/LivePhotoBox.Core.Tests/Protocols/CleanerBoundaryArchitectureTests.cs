using System;
using System.Linq;
using System.Reflection;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Protocols.Cleaning;
using Xunit;

namespace LivePhotoBox.Core.Tests.Protocols;

public class CleanerBoundaryArchitectureTests
{
    [Fact]
    public void NativeCleanService_IsInternal_NotPublic()
    {
        var type = typeof(NativeCleanService);
        Assert.False(type.IsPublic, "NativeCleanService must not be public.");
        Assert.True(type.IsNotPublic, "NativeCleanService must be internal.");
    }

    [Fact]
    public void NativeCleanService_HasNoPublicConstructorsOrMethods()
    {
        var type = typeof(NativeCleanService);
        var publicMethods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
        var publicCtors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);

        Assert.Empty(publicMethods);
        Assert.Empty(publicCtors);
    }

    [Fact]
    public void NativeMethods_IsInternal_NotPublic()
    {
        var type = typeof(NativeMethods);
        Assert.False(type.IsPublic, "NativeMethods must not be public.");
        Assert.True(type.IsNotPublic, "NativeMethods must be internal.");
    }

    [Fact]
    public void NativeMethods_HasNoLegacyUnplannedCleanPInvoke()
    {
        var type = typeof(NativeMethods);
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
        
        var legacyCleanMethod = methods.FirstOrDefault(m => m.Name.Equals("CleanSourceProtocol", StringComparison.OrdinalIgnoreCase) ||
                                                           m.Name.Equals("lpb_clean_source_protocol", StringComparison.OrdinalIgnoreCase));
        Assert.Null(legacyCleanMethod);
    }

    [Fact]
    public void PublicApi_DoesNotExpose_NativeCleanupStructs()
    {
        var coreAssembly = typeof(SourceProtocolCleaner).Assembly;
        var publicTypes = coreAssembly.GetExportedTypes();

        foreach (var pubType in publicTypes)
        {
            var methods = pubType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            foreach (var method in methods)
            {
                Assert.False(method.ReturnType.Name.Contains("NativeCleanupAction"), 
                    $"{pubType.FullName}.{method.Name} exposes NativeCleanupAction as return type.");
                Assert.False(method.ReturnType.Name.Contains("NativeCleanupArtifactBinding"), 
                    $"{pubType.FullName}.{method.Name} exposes NativeCleanupArtifactBinding as return type.");

                foreach (var param in method.GetParameters())
                {
                    Assert.False(param.ParameterType.Name.Contains("NativeCleanupAction"), 
                        $"{pubType.FullName}.{method.Name} exposes NativeCleanupAction as parameter {param.Name}.");
                    Assert.False(param.ParameterType.Name.Contains("NativeCleanupArtifactBinding"), 
                        $"{pubType.FullName}.{method.Name} exposes NativeCleanupArtifactBinding as parameter {param.Name}.");
                }
            }
        }
    }

    [Fact]
    public void SourceProtocolCleaner_HasNoPublicTestSeams()
    {
        var type = typeof(SourceProtocolCleaner);

        // Check public constructors: only optional ISourceInspector allowed
        var publicCtors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        foreach (var ctor in publicCtors)
        {
            var parameters = ctor.GetParameters();
            foreach (var param in parameters)
            {
                Assert.True(
                    param.ParameterType == typeof(ISourceInspector) || param.ParameterType == typeof(object),
                    $"Public constructor has unexpected parameter '{param.Name}' of type {param.ParameterType.FullName}");
            }
        }

        // Check public properties and events: no test seams exposed publicly
        var publicProps = type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
        foreach (var prop in publicProps)
        {
            Assert.DoesNotContain("Hook", prop.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Test", prop.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Seam", prop.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Invoker", prop.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Staging", prop.Name, StringComparison.OrdinalIgnoreCase);
        }

        var publicEvents = type.GetEvents(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
        Assert.Empty(publicEvents);
    }

    [Fact]
    public void Only_SourceProtocolCleaner_References_NativeCleanService()
    {
        var coreAssembly = typeof(SourceProtocolCleaner).Assembly;
        var allTypes = coreAssembly.GetTypes();

        foreach (var type in allTypes)
        {
            if (IsCleanerOrService(type))
            {
                continue;
            }

            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            foreach (var method in methods)
            {
                var referencedTypes = GetReferencedTypesInMethod(method);
                foreach (var refType in referencedTypes)
                {
                    Assert.False(
                        refType == typeof(NativeCleanService) || refType.DeclaringType == typeof(NativeCleanService),
                        $"Type '{type.FullName}' in method '{method.Name}' illegally calls or references NativeCleanService.");
                }
            }
        }
    }

    private static bool IsCleanerOrService(Type type)
    {
        if (type == typeof(NativeCleanService) || type == typeof(SourceProtocolCleaner))
            return true;
        if (type.DeclaringType != null && IsCleanerOrService(type.DeclaringType))
            return true;
        if (type.FullName != null && 
            (type.FullName.StartsWith("LivePhotoBox.Interop.NativeCleanService") ||
             type.FullName.StartsWith("LivePhotoBox.Protocols.Cleaning.SourceProtocolCleaner")))
            return true;
        return false;
    }

    private static IEnumerable<Type> GetReferencedTypesInMethod(MethodInfo method)
    {
        var body = method.GetMethodBody();
        if (body == null) yield break;

        foreach (var local in body.LocalVariables)
        {
            yield return local.LocalType;
        }

        var il = body.GetILAsByteArray();
        if (il == null || il.Length < 5) yield break;

        var module = method.Module;
        for (int i = 0; i <= il.Length - 5; i++)
        {
            byte op = il[i];
            bool isCall = (op == 0x28 || op == 0x6F || op == 0x73); // call, callvirt, newobj
            int tokenOffset = i + 1;

            if (!isCall && op == 0xFE && i <= il.Length - 6)
            {
                byte op2 = il[i + 1];
                if (op2 == 0x06 || op2 == 0x07) // ldftn, ldvirtftn
                {
                    isCall = true;
                    tokenOffset = i + 2;
                }
            }

            if (isCall)
            {
                int token = BitConverter.ToInt32(il, tokenOffset);
                MemberInfo? member = null;
                try
                {
                    member = module.ResolveMember(token);
                }
                catch
                {
                    // token may not be a member or might be from another scope
                }

                if (member != null)
                {
                    if (member is MethodBase mb && mb.DeclaringType != null)
                    {
                        yield return mb.DeclaringType;
                    }
                    else if (member is Type t)
                    {
                        yield return t;
                    }
                    else if (member.DeclaringType != null)
                    {
                        yield return member.DeclaringType;
                    }
                }
            }
        }
    }
}
