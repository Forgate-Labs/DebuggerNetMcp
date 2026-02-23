using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using DebuggerNetMcp.Core.Interop;

namespace DebuggerNetMcp.Core.Engine;

// CorElementType is not defined in ICorDebug.cs, so we define it here.
// Values match ECMA-335 table II.23.1.16 and the CorElementType enum in corhdr.h.
internal enum CorElementType : uint
{
    End         = 0x00,
    Void        = 0x01,
    Boolean     = 0x02,
    Char        = 0x03,
    I1          = 0x04,
    U1          = 0x05,
    I2          = 0x06,
    U2          = 0x07,
    I4          = 0x08,
    U4          = 0x09,
    I8          = 0x0A,
    U8          = 0x0B,
    R4          = 0x0C,
    R8          = 0x0D,
    String      = 0x0E,
    Ptr         = 0x0F,
    ByRef       = 0x10,
    ValueType   = 0x11,
    Class       = 0x12,
    Var         = 0x13,
    Array       = 0x14,
    GenericInst = 0x15,
    TypedByRef  = 0x16,
    I           = 0x18,
    U           = 0x19,
    FnPtr       = 0x1B,
    Object      = 0x1C,
    SzArray     = 0x1D,
    MVar        = 0x1E,
    CModReqd    = 0x1F,
    CModOpt     = 0x20,
    Internal    = 0x21,
    Max         = 0x22,
    Modifier    = 0x40,
    Sentinel    = 0x41,
    Pinned      = 0x45
}

/// <summary>
/// Reads an ICorDebugValue recursively, returning a VariableInfo tree up to depth 3.
/// Each CorElementType is dispatched to a dedicated handler. Errors in any single
/// handler are caught and surfaced in the Value field rather than propagated.
/// </summary>
internal static class VariableReader
{
    private const int MaxDepth = 3;
    private const uint MaxArrayElements = 10;

    /// <summary>
    /// Reads a debug value and returns its human-readable representation.
    /// </summary>
    /// <param name="name">Variable or field name.</param>
    /// <param name="value">The ICorDebugValue to inspect.</param>
    /// <param name="depth">Current recursion depth (0 = top level).</param>
    /// <returns>A VariableInfo with Type, Value, and optional Children.</returns>
    public static VariableInfo ReadValue(string name, ICorDebugValue value, int depth = 0)
    {
        if (depth > MaxDepth)
            return new VariableInfo(name, "...", "...", Array.Empty<VariableInfo>());

        uint elementTypeRaw;
        try
        {
            value.GetType(out elementTypeRaw);
        }
        catch
        {
            // GetType() failed — the returned COM object may support a derived interface
            // but not ICorDebugValue directly. Try the most specific interfaces first.

            // Try ICorDebugStringValue directly
            try
            {
                var sv = (ICorDebugStringValue)value;
                return ReadString(name, sv);
            }
            catch { }

            // Try ICorDebugArrayValue directly
            try
            {
                var av = (ICorDebugArrayValue)value;
                return ReadArray(name, av, depth);
            }
            catch { }

            // Try ICorDebugReferenceValue (may wrap null or need dereference)
            try
            {
                var refFallback = (ICorDebugReferenceValue)value;
                refFallback.IsNull(out int isNullFb);
                if (isNullFb != 0) return new VariableInfo(name, "object", "null", Array.Empty<VariableInfo>());
                refFallback.Dereference(out ICorDebugValue derefFb);
                return ReadValue(name, derefFb, depth);
            }
            catch { }

            return new VariableInfo(name, "?", "<unreadable reference>", Array.Empty<VariableInfo>());
        }

        var elementType = (CorElementType)elementTypeRaw;

        try
        {
            return elementType switch
            {
                CorElementType.Boolean  => ReadBoolean(name, value),
                CorElementType.Char     => ReadChar(name, value),
                CorElementType.I1       => ReadI1(name, value),
                CorElementType.U1       => ReadU1(name, value),
                CorElementType.I2       => ReadI2(name, value),
                CorElementType.U2       => ReadU2(name, value),
                CorElementType.I4       => ReadI4(name, value),
                CorElementType.U4       => ReadU4(name, value),
                CorElementType.I8       => ReadI8(name, value),
                CorElementType.U8       => ReadU8(name, value),
                CorElementType.R4       => ReadR4(name, value),
                CorElementType.R8       => ReadR8(name, value),
                CorElementType.String   => ReadString(name, value),
                CorElementType.SzArray  => ReadArray(name, value, depth),
                CorElementType.Array    => ReadArray(name, value, depth),
                CorElementType.Object   => ReadObject(name, value, depth, "object"),
                CorElementType.Class    => ReadObject(name, value, depth, "object"),
                CorElementType.ValueType => ReadObject(name, value, depth, "struct"),
                _                       => new VariableInfo(name, $"<{elementType}>", "?", Array.Empty<VariableInfo>())
            };
        }
        catch (Exception ex)
        {
            return new VariableInfo(name, "?", $"<error: {ex.Message}>", Array.Empty<VariableInfo>());
        }
    }

    // ---------------------------------------------------------------------------
    // Primitive readers
    // ---------------------------------------------------------------------------

    private static byte[] ReadGenericBytes(ICorDebugValue value)
    {
        var generic = (ICorDebugGenericValue)value;
        value.GetSize(out uint size);
        var buf = new byte[size];
        generic.GetValue(buf);
        return buf;
    }

    private static VariableInfo ReadBoolean(string name, ICorDebugValue value)
    {
        var buf = ReadGenericBytes(value);
        var str = buf[0] != 0 ? "true" : "false";
        return new VariableInfo(name, "bool", str, Array.Empty<VariableInfo>());
    }

    private static VariableInfo ReadChar(string name, ICorDebugValue value)
    {
        var buf = ReadGenericBytes(value);
        var ch = BitConverter.ToChar(buf, 0);
        return new VariableInfo(name, "char", ch.ToString(), Array.Empty<VariableInfo>());
    }

    private static VariableInfo ReadI1(string name, ICorDebugValue value)
    {
        var buf = ReadGenericBytes(value);
        return new VariableInfo(name, "sbyte", ((sbyte)buf[0]).ToString(), Array.Empty<VariableInfo>());
    }

    private static VariableInfo ReadU1(string name, ICorDebugValue value)
    {
        var buf = ReadGenericBytes(value);
        return new VariableInfo(name, "byte", buf[0].ToString(), Array.Empty<VariableInfo>());
    }

    private static VariableInfo ReadI2(string name, ICorDebugValue value)
    {
        var buf = ReadGenericBytes(value);
        return new VariableInfo(name, "short", BitConverter.ToInt16(buf, 0).ToString(), Array.Empty<VariableInfo>());
    }

    private static VariableInfo ReadU2(string name, ICorDebugValue value)
    {
        var buf = ReadGenericBytes(value);
        return new VariableInfo(name, "ushort", BitConverter.ToUInt16(buf, 0).ToString(), Array.Empty<VariableInfo>());
    }

    private static VariableInfo ReadI4(string name, ICorDebugValue value)
    {
        var buf = ReadGenericBytes(value);
        return new VariableInfo(name, "int", BitConverter.ToInt32(buf, 0).ToString(), Array.Empty<VariableInfo>());
    }

    private static VariableInfo ReadU4(string name, ICorDebugValue value)
    {
        var buf = ReadGenericBytes(value);
        return new VariableInfo(name, "uint", BitConverter.ToUInt32(buf, 0).ToString(), Array.Empty<VariableInfo>());
    }

    private static VariableInfo ReadI8(string name, ICorDebugValue value)
    {
        var buf = ReadGenericBytes(value);
        return new VariableInfo(name, "long", BitConverter.ToInt64(buf, 0).ToString(), Array.Empty<VariableInfo>());
    }

    private static VariableInfo ReadU8(string name, ICorDebugValue value)
    {
        var buf = ReadGenericBytes(value);
        return new VariableInfo(name, "ulong", BitConverter.ToUInt64(buf, 0).ToString(), Array.Empty<VariableInfo>());
    }

    private static VariableInfo ReadR4(string name, ICorDebugValue value)
    {
        var buf = ReadGenericBytes(value);
        return new VariableInfo(name, "float", BitConverter.ToSingle(buf, 0).ToString(), Array.Empty<VariableInfo>());
    }

    private static VariableInfo ReadR8(string name, ICorDebugValue value)
    {
        var buf = ReadGenericBytes(value);
        return new VariableInfo(name, "double", BitConverter.ToDouble(buf, 0).ToString(), Array.Empty<VariableInfo>());
    }

    // ---------------------------------------------------------------------------
    // String reader
    // ---------------------------------------------------------------------------

    private static VariableInfo ReadString(string name, ICorDebugValue value)
    {
        // GetFieldValue may return a reference (ICorDebugReferenceValue) even when GetType() says String.
        // CoreCLR on Linux sometimes returns the field's value as an indirect reference.
        // Try deref first; if that fails, cast directly.
        ICorDebugStringValue? strVal = null;
        try { strVal = (ICorDebugStringValue)value; }
        catch
        {
            try
            {
                var rv = (ICorDebugReferenceValue)value;
                rv.IsNull(out int isNull);
                if (isNull != 0) return new VariableInfo(name, "string", "null", Array.Empty<VariableInfo>());
                rv.Dereference(out var derefed);
                strVal = (ICorDebugStringValue)derefed;
            }
            catch
            {
                return new VariableInfo(name, "string", "<unavailable>", Array.Empty<VariableInfo>());
            }
        }

        strVal.GetLength(out uint len);

        string result;
        if (len == 0)
        {
            result = "\"\"";
        }
        else
        {
            // ICorDebugStringValue.GetString uses IntPtr (fixed in Plan 02-02 for SYSLIB1051).
            // Allocate a native buffer for the UTF-16 string (len chars + null terminator).
            IntPtr buf = Marshal.AllocHGlobal((int)(len + 1) * 2);
            try
            {
                strVal.GetString(len, out _, buf);
                var str = Marshal.PtrToStringUni(buf, (int)len);
                result = $"\"{str}\"";
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        return new VariableInfo(name, "string", result, Array.Empty<VariableInfo>());
    }

    // ---------------------------------------------------------------------------
    // Array reader
    // ---------------------------------------------------------------------------

    private static VariableInfo ReadArray(string name, ICorDebugValue value, int depth)
    {
        // Similar to ReadString: dereference first if direct cast fails.
        ICorDebugArrayValue? arrVal = null;
        try { arrVal = (ICorDebugArrayValue)value; }
        catch
        {
            try
            {
                var rv = (ICorDebugReferenceValue)value;
                rv.IsNull(out int isNull);
                if (isNull != 0) return new VariableInfo(name, "array", "null", Array.Empty<VariableInfo>());
                rv.Dereference(out var derefed);
                arrVal = (ICorDebugArrayValue)derefed;
            }
            catch
            {
                return new VariableInfo(name, "array", "<unavailable>", Array.Empty<VariableInfo>());
            }
        }
        arrVal.GetCount(out uint count);
        arrVal.GetElementType(out uint elemTypeRaw);

        var children = new List<VariableInfo>();

        if (depth < MaxDepth)
        {
            uint limit = Math.Min(count, MaxArrayElements);
            for (uint i = 0; i < limit; i++)
            {
                try
                {
                    arrVal.GetElementAtPosition(i, out var elem);
                    children.Add(ReadValue($"[{i}]", elem, depth + 1));
                }
                catch (Exception ex)
                {
                    children.Add(new VariableInfo($"[{i}]", "?", $"<error: {ex.Message}>", Array.Empty<VariableInfo>()));
                }
            }
        }

        var typeName = $"{(CorElementType)elemTypeRaw}[]";
        return new VariableInfo(name, typeName, $"[{count} elements]", children);
    }

    // ---------------------------------------------------------------------------
    // Object / value-type reader
    // ---------------------------------------------------------------------------

    private static VariableInfo ReadObject(string name, ICorDebugValue value, int depth, string typeName)
    {
        if (depth > MaxDepth)
            return new VariableInfo(name, typeName, "<max depth>", Array.Empty<VariableInfo>());

        // For reference types (Object, Class): dereference first before casting to ICorDebugObjectValue.
        // For value types (ValueType/struct): no dereference needed — cast directly.
        ICorDebugValue actualValue = value;

        if (typeName != "struct")
        {
            try
            {
                var refVal = (ICorDebugReferenceValue)value;
                refVal.IsNull(out int isNull);
                if (isNull != 0)
                    return new VariableInfo(name, typeName, "null", Array.Empty<VariableInfo>());

                refVal.Dereference(out actualValue);
            }
            catch (InvalidCastException)
            {
                // Value type — use as-is (actualValue already set to value).
            }
            catch (Exception)
            {
                // Dereference failed — use as-is.
            }
        }

        if (actualValue is not ICorDebugObjectValue objVal)
            return new VariableInfo(name, typeName, "<not an object>", Array.Empty<VariableInfo>());

        return ReadObjectFields(name, typeName, objVal, depth);
    }

    private static VariableInfo ReadObjectFields(string name, string typeName, ICorDebugObjectValue objVal, int depth)
    {
        try
        {
            objVal.GetClass(out ICorDebugClass cls);
            cls.GetToken(out uint typedefToken);
            cls.GetModule(out ICorDebugModule module);

            // Get DLL path from module (to open PE metadata without COM interop)
            string dllPath = GetModulePath(module);
            if (string.IsNullOrEmpty(dllPath))
                return new VariableInfo(name, typeName, "<no module path>", Array.Empty<VariableInfo>());

            var children = new List<VariableInfo>();

            // Walk the inheritance chain: collect fields from the concrete type and all base types
            // in the same module (base types in other modules are skipped — BCL internals).
            uint currentToken = typedefToken;
            while (currentToken != 0)
            {
                var fieldMap = ReadInstanceFieldsFromPE(dllPath, currentToken);

                // Get the ICorDebugClass for this level to pass to GetFieldValue
                ICorDebugClass? levelClass = null;
                try { module.GetClassFromToken(currentToken, out levelClass); }
                catch { /* not in this module or unavailable */ }

                if (levelClass != null)
                {
                    foreach (var (fieldToken, fieldName) in fieldMap)
                    {
                        // Determine display name for this field:
                        // - "<>..." prefix → compiler infrastructure — skip
                        // - "<Name>k__BackingField" / "<field>N__M" → display extracted name
                        // - other names → display as-is
                        string displayName;
                        if (fieldName.StartsWith("<>"))
                            continue;
                        else if (fieldName.StartsWith("<"))
                        {
                            int closeAngle = fieldName.IndexOf('>');
                            if (closeAngle > 1)
                                displayName = fieldName.Substring(1, closeAngle - 1);
                            else
                                continue;
                        }
                        else
                        {
                            displayName = fieldName;
                        }

                        try
                        {
                            objVal.GetFieldValue(levelClass, fieldToken, out ICorDebugValue fieldVal);
                            children.Add(ReadValue(displayName, fieldVal, depth + 1));
                        }
                        catch { /* field not available at this point */ }
                    }
                }

                // Advance to base type (TypeDefinitionHandle only — cross-module refs not followed)
                currentToken = GetBaseTypeToken(dllPath, currentToken);
            }

            string displayTypeName = GetTypeName(dllPath, typedefToken);
            return new VariableInfo(name, displayTypeName, $"{{fields: {children.Count}}}", children);
        }
        catch (Exception ex)
        {
            return new VariableInfo(name, typeName, $"<error: {ex.Message}>", Array.Empty<VariableInfo>());
        }
    }

    /// <summary>Returns the module file path from an ICorDebugModule.</summary>
    private static string GetModulePath(ICorDebugModule module)
    {
        uint nameLen = 512;
        IntPtr namePtr = Marshal.AllocHGlobal((int)(nameLen * 2));
        try
        {
            module.GetName(nameLen, out _, namePtr);
            return Marshal.PtrToStringUni(namePtr) ?? string.Empty;
        }
        catch { return string.Empty; }
        finally { Marshal.FreeHGlobal(namePtr); }
    }

    /// <summary>
    /// Returns the TypeDef token of the base type of <paramref name="typedefToken"/>
    /// in the same assembly. Returns 0 if the base is in another assembly or is System.Object.
    /// </summary>
    private static uint GetBaseTypeToken(string dllPath, uint typedefToken)
    {
        try
        {
            using var peReader = new PEReader(File.OpenRead(dllPath));
            var metadata = peReader.GetMetadataReader();
            int rowNumber = (int)(typedefToken & 0x00FFFFFF);
            var typeHandle = MetadataTokens.TypeDefinitionHandle(rowNumber);
            var typeDef = metadata.GetTypeDefinition(typeHandle);
            var baseTypeHandle = typeDef.BaseType;
            if (baseTypeHandle.IsNil) return 0;
            if (baseTypeHandle.Kind == HandleKind.TypeDefinition)
            {
                // Base type is in the same assembly
                var baseTypeDef = metadata.GetTypeDefinition((TypeDefinitionHandle)baseTypeHandle);
                string baseTypeName = metadata.GetString(baseTypeDef.Name);
                // Stop at System.Object — it has no useful fields
                if (baseTypeName == "Object") return 0;
                return (uint)MetadataTokens.GetToken(baseTypeHandle);
            }
            // TypeReference (cross-assembly) — not followed
            return 0;
        }
        catch { return 0; }
    }

    /// <summary>
    /// Reads all instance field tokens and names from PE metadata for a given TypeDef.
    /// Uses System.Reflection.Metadata — no COM interop required.
    /// </summary>
    private static Dictionary<uint, string> ReadInstanceFieldsFromPE(string dllPath, uint typedefToken)
    {
        var result = new Dictionary<uint, string>();
        try
        {
            using var peReader = new PEReader(File.OpenRead(dllPath));
            var metadata = peReader.GetMetadataReader();
            int rowNumber = (int)(typedefToken & 0x00FFFFFF);
            var typeHandle = MetadataTokens.TypeDefinitionHandle(rowNumber);
            var typeDef = metadata.GetTypeDefinition(typeHandle);
            foreach (var fieldHandle in typeDef.GetFields())
            {
                var field = metadata.GetFieldDefinition(fieldHandle);
                if ((field.Attributes & System.Reflection.FieldAttributes.Static) != 0) continue;
                uint fieldToken = (uint)MetadataTokens.GetToken(fieldHandle);
                string fieldName = metadata.GetString(field.Name);
                result[fieldToken] = fieldName;
            }
        }
        catch { }
        return result;
    }

    /// <summary>Returns the simple type name (e.g. "List`1", "Person") from PE metadata.</summary>
    private static string GetTypeName(string dllPath, uint typedefToken)
    {
        try
        {
            using var peReader = new PEReader(File.OpenRead(dllPath));
            var metadata = peReader.GetMetadataReader();
            int rowNumber = (int)(typedefToken & 0x00FFFFFF);
            var typeHandle = MetadataTokens.TypeDefinitionHandle(rowNumber);
            var typeDef = metadata.GetTypeDefinition(typeHandle);
            return metadata.GetString(typeDef.Name);
        }
        catch { return "object"; }
    }
}
