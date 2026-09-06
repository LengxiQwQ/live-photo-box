using System;
using System.Linq;
using System.Reflection;
using LivePhotoBox.Interop;
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
    public void Only_SourceProtocolCleaner_References_NativeCleanService()
    {
        var coreAssembly = typeof(SourceProtocolCleaner).Assembly;
        var allTypes = coreAssembly.GetTypes();

        foreach (var type in allTypes)
        {
            if (type == typeof(NativeCleanService) || 
                type.DeclaringType == typeof(NativeCleanService) ||
                type.FullName?.StartsWith("LivePhotoBox.Interop.NativeCleanService") == true)
            {
                continue;
            }

            if (type == typeof(SourceProtocolCleaner) || 
                type.DeclaringType == typeof(SourceProtocolCleaner) ||
                type.FullName?.StartsWith("LivePhotoBox.Protocols.Cleaning.SourceProtocolCleaner") == true)
            {
                continue;
            }

            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            foreach (var method in methods)
            {
                var body = method.GetMethodBody();
                if (body == null) continue;

                foreach (var local in body.LocalVariables)
                {
                    Assert.NotEqual(typeof(NativeCleanService), local.LocalType);
                }
            }
        }
    }
}
