using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using LivePhotoBox.Interop;
using LivePhotoBox.Media;
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

    [Fact]
    public void TargetedPostCleanVerifier_HasNoBinaryParsingMethods()
    {
        var type = typeof(TargetedPostCleanVerifier);
        var methods = type.GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.DeclaredOnly);
        
        var forbiddenNames = new[] 
        { 
            "CheckQuickTime", "CheckXmpProperty", "ParseXmp", 
            "ParseMakerNote", "ParseApple", "ParseGoogle", "ParseSamsung"
        };
        
        foreach (var method in methods)
        {
            foreach (var forbidden in forbiddenNames)
            {
                Assert.False(
                    method.Name.Contains(forbidden, StringComparison.OrdinalIgnoreCase),
                    $"TargetedPostCleanVerifier still contains C# protocol truth method '{method.Name}'. "
                    + "Post-clean verification must delegate to ISourceInspector (Native).");
            }
        }
    }

    [Fact]
    public void TargetedPostCleanVerifier_VerifyMethod_DoesNotUseBinaryParsing()
    {
        var type = typeof(TargetedPostCleanVerifier);
        var methods = type.GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.DeclaredOnly);
        
        // Protocol-truth binary types that must NOT appear in TargetedPostCleanVerifier
        var forbiddenTypes = new Type[]
        {
            typeof(System.Buffers.Binary.BinaryPrimitives),
            typeof(System.Xml.Linq.XDocument),
            typeof(System.Xml.Linq.XNamespace),
        };
        
        foreach (var method in methods)
        {
            var referencedTypes = GetReferencedTypesInMethod(method).ToList();
            foreach (var forbidden in forbiddenTypes)
            {
                Assert.False(
                    referencedTypes.Contains(forbidden),
                    $"TargetedPostCleanVerifier.{method.Name} references {forbidden.FullName}. "
                    + "This indicates C# binary/XML parsing for protocol truth. "
                    + "Post-clean protocol verification must use ISourceInspector (Native).");
            }
        }
    }

    [Fact]
    public void MetadataPreservationVerifier_HeifLocation_DoesNotUseHeifBoxParserFallback()
    {
        var type = typeof(MetadataPreservationVerifier);
        var methodsToCheck = new[] { "TryLocateHeicExifItem", "TryLocateHeicXmpItem" };
        
        // Get HeifBoxParser type - it's internal so use assembly lookup
        var coreAssembly = typeof(MetadataPreservationVerifier).Assembly;
        var heifBoxParserType = coreAssembly.GetType("LivePhotoBox.Services.Protocols.HeifBoxParser", throwOnError: false);
        if (heifBoxParserType == null)
        {
            // If HeifBoxParser was removed entirely, test trivially passes
            return;
        }
        
        foreach (var methodName in methodsToCheck)
        {
            var method = type.GetMethod(methodName,
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);
            
            var referencedTypes = GetReferencedTypesInMethod(method).ToList();
            Assert.False(
                referencedTypes.Contains(heifBoxParserType),
                $"MetadataPreservationVerifier.{methodName} still references HeifBoxParser. "
                + "This is a silent C# fallback that violates Native Data Plane authority. "
                + "TryLocateHeicExifItem/XmpItem must use NativeHeifBoxParser only (fail closed on failure).");
        }
    }

    [Fact]
    public void HeifBoxParser_IsUsedOnlyInKnownLegacyTargetWriters()
    {
        var coreAssembly = typeof(SourceProtocolCleaner).Assembly;
        var heifBoxParserType = coreAssembly.GetType("LivePhotoBox.Services.Protocols.HeifBoxParser", throwOnError: false);
        if (heifBoxParserType == null) return; // If removed, test passes
        
        var allTypes = coreAssembly.GetTypes();
        
        // Only these pre-existing P9 Target Writers are allowed to use HeifBoxParser
        var allowedUsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "LivePhotoBox.Services.Protocols.AppleMakerNoteWriter",
            "LivePhotoBox.Services.Protocols.HeifAuxImageWriter",
            "LivePhotoBox.Services.Protocols.HeifBoxParser",
        };
        
        foreach (var type in allTypes)
        {
            if (allowedUsers.Contains(type.FullName ?? "")) continue;
            if (type.FullName != null && type.FullName.StartsWith("LivePhotoBox.Services.Protocols.AppleMakerNoteWriter+")) continue;
            if (type.FullName != null && type.FullName.StartsWith("LivePhotoBox.Services.Protocols.HeifAuxImageWriter+")) continue;
            
            var methods = type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.DeclaredOnly);
            
            foreach (var method in methods)
            {
                var referencedTypes = GetReferencedTypesInMethod(method);
                Assert.False(
                    referencedTypes.Contains(heifBoxParserType),
                    $"Type '{type.FullName}' method '{method.Name}' uses HeifBoxParser. "
                    + "HeifBoxParser is restricted to pre-existing P9 Target Writers (AppleMakerNoteWriter, HeifAuxImageWriter). "
                    + "New preservation/verification/pipeline code must use NativeHeifBoxParser instead.");
            }
        }
    }

    [Fact]
    public void NeutralMediaService_GainMapReassembly_UsesNativeMediaService()
    {
        var neutralType = typeof(NeutralMediaService);
        var nativeMediaServiceType = typeof(NativeMediaService);
        
        // Find the private ReassembleJpegGainMapAsync method
        var reassembleMethod = neutralType.GetMethod(
            "ReassembleJpegGainMapAsync",
            BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
        Assert.NotNull(reassembleMethod);
        
        var referencedTypes = GetReferencedTypesInMethod(reassembleMethod).ToList();
        Assert.True(
            referencedTypes.Contains(nativeMediaServiceType),
            "NeutralMediaService.ReassembleJpegGainMapAsync must call NativeMediaService for GainMap reassembly. "
            + "Raw C# byte-append (FileStream.CopyToAsync) is not acceptable as the production GainMap join path.");
    }

    private static IEnumerable<Type> GetReferencedTypesInMethod(MethodInfo method)
    {
        var asyncAttr = method.GetCustomAttribute<AsyncStateMachineAttribute>();
        if (asyncAttr != null)
        {
            var moveNext = asyncAttr.StateMachineType.GetMethod("MoveNext", BindingFlags.NonPublic | BindingFlags.Instance);
            if (moveNext != null)
            {
                foreach (var t in GetReferencedTypesInMethod(moveNext))
                {
                    yield return t;
                }
            }
        }

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
