using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace DebuggerNetMcp.Core.Engine;

/// <summary>
/// Provides source-line to IL-offset mapping by reading Portable PDB data
/// (embedded or associated file) from a compiled .NET assembly.
/// </summary>
internal static class PdbReader
{
    /// <summary>
    /// Finds the first sequence point matching the given source file and line number.
    /// Returns the method token (0x06000000 | 1-based row number) and IL offset.
    /// </summary>
    /// <param name="dllPath">Absolute path to the compiled .NET assembly (.dll).</param>
    /// <param name="sourceFile">Source file name or path. Matched with EndsWith or filename equality.</param>
    /// <param name="line">1-based source line number.</param>
    /// <returns>A tuple of (methodToken, ilOffset).</returns>
    /// <exception cref="FileNotFoundException">Thrown when no PDB data is found for the assembly.</exception>
    /// <exception cref="InvalidOperationException">Thrown when no sequence point matches the given file and line.</exception>
    public static (int methodToken, int ilOffset) FindLocation(string dllPath, string sourceFile, int line)
    {
        using var peReader = new PEReader(File.OpenRead(dllPath));

        using var pdbProvider = OpenPdbProvider(peReader, dllPath);
        var pdbMetadata = pdbProvider.GetMetadataReader();

        foreach (var methodDebugHandle in pdbMetadata.MethodDebugInformation)
        {
            var debugInfo = pdbMetadata.GetMethodDebugInformation(methodDebugHandle);
            foreach (var sp in debugInfo.GetSequencePoints())
            {
                if (sp.IsHidden) continue;
                var doc = pdbMetadata.GetDocument(sp.Document);
                var docName = pdbMetadata.GetString(doc.Name);
                if (MatchesSourceFile(docName, sourceFile) && sp.StartLine == line)
                {
                    int rowNumber = MetadataTokens.GetRowNumber(methodDebugHandle);
                    int methodToken = 0x06000000 | rowNumber;
                    return (methodToken, sp.Offset);
                }
            }
        }

        throw new InvalidOperationException($"No sequence point found at {sourceFile}:{line}");
    }

    /// <summary>
    /// Finds all sequence points matching the given source file and line number.
    /// Some source lines (e.g., in async methods) map to multiple sequence points.
    /// Returns an empty list if no matches are found.
    /// </summary>
    /// <param name="dllPath">Absolute path to the compiled .NET assembly (.dll).</param>
    /// <param name="sourceFile">Source file name or path. Matched with EndsWith or filename equality.</param>
    /// <param name="line">1-based source line number.</param>
    /// <returns>List of (methodToken, ilOffset) tuples for all matching sequence points.</returns>
    public static List<(int methodToken, int ilOffset)> FindAllLocations(string dllPath, string sourceFile, int line)
    {
        var results = new List<(int methodToken, int ilOffset)>();

        using var peReader = new PEReader(File.OpenRead(dllPath));

        MetadataReaderProvider? pdbProvider = null;
        try
        {
            pdbProvider = OpenPdbProvider(peReader, dllPath);
        }
        catch (FileNotFoundException)
        {
            return results;
        }

        using (pdbProvider)
        {
            var pdbMetadata = pdbProvider.GetMetadataReader();

            foreach (var methodDebugHandle in pdbMetadata.MethodDebugInformation)
            {
                var debugInfo = pdbMetadata.GetMethodDebugInformation(methodDebugHandle);
                foreach (var sp in debugInfo.GetSequencePoints())
                {
                    if (sp.IsHidden) continue;
                    var doc = pdbMetadata.GetDocument(sp.Document);
                    var docName = pdbMetadata.GetString(doc.Name);
                    if (MatchesSourceFile(docName, sourceFile) && sp.StartLine == line)
                    {
                        int rowNumber = MetadataTokens.GetRowNumber(methodDebugHandle);
                        int methodToken = 0x06000000 | rowNumber;
                        results.Add((methodToken, sp.Offset));
                    }
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Returns the method name and a mapping from field token to field name for all
    /// instance fields of the type containing the given method. Used to read async
    /// state machine fields (counter, isActive, etc.) from MoveNext's 'this' argument.
    /// Returns method name and an empty dictionary if metadata cannot be read.
    /// </summary>
    /// <param name="dllPath">Absolute path to the compiled .NET assembly (.dll).</param>
    /// <param name="methodToken">The method's metadata token (e.g., 0x06000001).</param>
    public static (string methodName, Dictionary<uint, string> fields) GetMethodTypeFields(string dllPath, int methodToken)
    {
        try
        {
            using var peReader = new PEReader(File.OpenRead(dllPath));
            var metadata = peReader.GetMetadataReader();

            int rowNumber = methodToken & 0x00FFFFFF;
            var methodHandle = MetadataTokens.MethodDefinitionHandle(rowNumber);
            var methodDef = metadata.GetMethodDefinition(methodHandle);
            string methodName = metadata.GetString(methodDef.Name);

            var fields = new Dictionary<uint, string>();
            var typeHandle = methodDef.GetDeclaringType();
            if (typeHandle.IsNil) return (methodName, fields);

            var typeDef = metadata.GetTypeDefinition(typeHandle);
            foreach (var fieldHandle in typeDef.GetFields())
            {
                var field = metadata.GetFieldDefinition(fieldHandle);
                // Skip static fields
                if ((field.Attributes & System.Reflection.FieldAttributes.Static) != 0) continue;

                uint fieldToken = (uint)MetadataTokens.GetToken(fieldHandle);
                string fieldName = metadata.GetString(field.Name);
                fields[fieldToken] = fieldName;
            }

            return (methodName, fields);
        }
        catch
        {
            return ("", new Dictionary<uint, string>());
        }
    }

    /// <summary>
    /// Returns a mapping from local variable slot index to name for the given method.
    /// Reads MethodDebugInformation from the PDB and enumerates LocalScope variables.
    /// Returns an empty dictionary if the method is not found or PDB is not available.
    /// </summary>
    /// <param name="dllPath">Absolute path to the compiled .NET assembly (.dll).</param>
    /// <param name="methodToken">The method's metadata token (e.g., 0x06000001).</param>
    /// <returns>Dictionary mapping slot index to variable name.</returns>
    public static Dictionary<int, string> GetLocalNames(string dllPath, int methodToken)
    {
        var result = new Dictionary<int, string>();

        try
        {
            using var peReader = new PEReader(File.OpenRead(dllPath));
            using var pdbProvider = OpenPdbProvider(peReader, dllPath);
            var pdbMetadata = pdbProvider.GetMetadataReader();

            // Build the MethodDebugInformationHandle from the method token row number
            int rowNumber = methodToken & 0x00FFFFFF;
            var methodHandle = MetadataTokens.MethodDefinitionHandle(rowNumber);
            var debugHandle = methodHandle.ToDebugInformationHandle();

            var debugInfo = pdbMetadata.GetMethodDebugInformation(debugHandle);

            // Enumerate local scopes for this method
            foreach (var localScopeHandle in pdbMetadata.GetLocalScopes(methodHandle))
            {
                var scope = pdbMetadata.GetLocalScope(localScopeHandle);
                foreach (var localVarHandle in scope.GetLocalVariables())
                {
                    var localVar = pdbMetadata.GetLocalVariable(localVarHandle);
                    string varName = pdbMetadata.GetString(localVar.Name);
                    int slotIndex = localVar.Index;
                    result.TryAdd(slotIndex, varName);  // first name wins if duplicates
                }
            }
        }
        catch (FileNotFoundException)
        {
            // PDB not found — return empty
        }
        catch (BadImageFormatException)
        {
            // Malformed PDB — return empty
        }
        catch (InvalidOperationException)
        {
            // Method not found in PDB — return empty
        }

        return result;
    }

    /// <summary>
    /// Returns the TypeDef token of the type that declares the given method.
    /// Returns 0 if the method token is invalid or the type cannot be found.
    /// </summary>
    /// <param name="dllPath">Absolute path to the compiled .NET assembly (.dll).</param>
    /// <param name="methodToken">The method's metadata token (e.g., 0x06000001).</param>
    public static uint GetDeclaringTypeToken(string dllPath, int methodToken)
    {
        try
        {
            using var peReader = new PEReader(File.OpenRead(dllPath));
            var metadata = peReader.GetMetadataReader();
            int rowNumber = methodToken & 0x00FFFFFF;
            var methodHandle = MetadataTokens.MethodDefinitionHandle(rowNumber);
            var methodDef = metadata.GetMethodDefinition(methodHandle);
            var typeHandle = methodDef.GetDeclaringType();
            if (typeHandle.IsNil) return 0;
            return (uint)MetadataTokens.GetToken(typeHandle);
        }
        catch { return 0; }
    }

    /// <summary>
    /// Searches all TypeDefinitions in the assembly for a type with the given simple name.
    /// Returns the TypeDef token if found, or 0 if not found.
    /// </summary>
    /// <param name="dllPath">Absolute path to the compiled .NET assembly (.dll).</param>
    /// <param name="typeName">Simple type name to search for (e.g., "MyClass").</param>
    public static uint FindTypeByName(string dllPath, string typeName)
    {
        try
        {
            using var peReader = new PEReader(File.OpenRead(dllPath));
            var metadata = peReader.GetMetadataReader();
            foreach (var typeHandle in metadata.TypeDefinitions)
            {
                var typeDef = metadata.GetTypeDefinition(typeHandle);
                string name = metadata.GetString(typeDef.Name);
                if (name == typeName)
                    return (uint)MetadataTokens.GetToken(typeHandle);
            }
        }
        catch { }
        return 0;
    }

    // ---------------------------------------------------------------------------
    // Private helpers
    // ---------------------------------------------------------------------------

    private static MetadataReaderProvider OpenPdbProvider(PEReader peReader, string dllPath)
    {
        var debugDir = peReader.ReadDebugDirectory();

        // Try embedded PDB first.
        var embeddedEntry = debugDir.FirstOrDefault(e => e.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);
        if (embeddedEntry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb)
        {
            return peReader.ReadEmbeddedPortablePdbDebugDirectoryData(embeddedEntry);
        }

        // Try associated .pdb file next.
        if (peReader.TryOpenAssociatedPortablePdb(dllPath, path => File.OpenRead(path),
                out var pdbProvider, out _) && pdbProvider != null)
        {
            return pdbProvider;
        }

        throw new FileNotFoundException($"PDB not found for {dllPath}");
    }

    /// <summary>
    /// Returns true if the document name from the PDB matches the requested source file.
    /// Handles both full absolute path comparisons and filename-only comparisons.
    /// </summary>
    private static bool MatchesSourceFile(string docName, string sourceFile)
    {
        // Full path suffix match (e.g., docName="/home/user/proj/src/Foo.cs", sourceFile="src/Foo.cs")
        if (docName.EndsWith(sourceFile, System.StringComparison.OrdinalIgnoreCase))
            return true;

        // Filename-only match (e.g., sourceFile="Foo.cs")
        if (Path.GetFileName(docName).Equals(Path.GetFileName(sourceFile), System.StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }
}
