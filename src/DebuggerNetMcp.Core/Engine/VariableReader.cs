using System;
using System.Collections.Generic;
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
        catch (Exception ex)
        {
            return new VariableInfo(name, "?", $"<error reading type: {ex.Message}>", Array.Empty<VariableInfo>());
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
        var strVal = (ICorDebugStringValue)value;
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
        var arrVal = (ICorDebugArrayValue)value;
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

        // Phase 2: return a placeholder for object field enumeration.
        // Full field enumeration via ICorDebugObjectValue.GetFieldValue requires knowing field
        // metadata tokens from ICorDebugModule.GetMetaDataInterface — this is the Phase 3 integration.
        // Phase 3 (DotnetDebugger.cs) will add metadata-driven field enumeration here.
        return new VariableInfo(name, typeName, "<object>", Array.Empty<VariableInfo>());
    }
}
