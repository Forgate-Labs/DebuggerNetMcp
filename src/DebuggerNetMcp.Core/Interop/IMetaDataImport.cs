using System.Runtime.InteropServices;

namespace DebuggerNetMcp.Core.Interop;

/// <summary>
/// Minimal subset of IMetaDataImport used for field enumeration during object variable inspection.
/// Uses [ComImport] (not [GeneratedComInterface]) because the real interface has ~70 methods;
/// declaring a partial vtable with [GeneratedComInterface] would produce an incorrect vtable layout.
/// The real IMetaDataImport GUID is used so Marshal.GetObjectForIUnknown returns the correct RCW.
/// </summary>
[ComImport]
[Guid("7DAC8207-D3AE-4C75-9B67-92801A497D44")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMetaDataImportMinimal
{
    // vtable slot 0 (after IUnknown): CloseEnum
    void CloseEnum(IntPtr hEnum);

    // vtable slot 1: CountEnum
    void CountEnum(IntPtr hEnum, out uint pulCount);

    // vtable slot 2: ResetEnum
    void ResetEnum(IntPtr hEnum, uint ulPos);

    // vtable slot 3: EnumTypeDefs
    void EnumTypeDefs(ref IntPtr phEnum,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] uint[] rTypeDefs,
        uint cMax,
        out uint pcTypeDefs);

    // vtable slot 4: EnumInterfaceImpls
    void EnumInterfaceImpls(ref IntPtr phEnum,
        uint td,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] uint[] rImpls,
        uint cMax,
        out uint pcImpls);

    // vtable slot 5: EnumTypeRefs
    void EnumTypeRefs(ref IntPtr phEnum,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] uint[] rTypeRefs,
        uint cMax,
        out uint pcTypeRefs);

    // vtable slot 6: FindTypeDefByName
    void FindTypeDefByName(
        [MarshalAs(UnmanagedType.LPWStr)] string szTypeDef,
        uint tkEnclosingClass,
        out uint ptd);

    // vtable slot 7: GetScopeProps
    void GetScopeProps(
        IntPtr szName,
        uint cchName,
        out uint pchName,
        out Guid pmvid);

    // vtable slot 8: GetModuleFromScope
    void GetModuleFromScope(out uint pmd);

    // vtable slot 9: GetTypeDefProps
    void GetTypeDefProps(
        uint td,
        IntPtr szTypeDef,
        uint cchTypeDef,
        out uint pchTypeDef,
        out uint pdwTypeDefFlags,
        out uint ptkExtends);

    // vtable slot 10: GetInterfaceImplProps
    void GetInterfaceImplProps(uint iiImpl, out uint pClass, out uint ptkIface);

    // vtable slot 11: GetTypeRefProps
    void GetTypeRefProps(
        uint tr,
        out uint ptkResolutionScope,
        IntPtr szName,
        uint cchName,
        out uint pchName);

    // vtable slot 12: ResolveTypeRef
    void ResolveTypeRef(uint tr, in Guid riid, out object ppIScope, out uint ptd);

    // vtable slot 13: EnumMembers
    void EnumMembers(ref IntPtr phEnum, uint cl,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] uint[] rMembers,
        uint cMax, out uint pcTokens);

    // vtable slot 14: EnumMembersWithName
    void EnumMembersWithName(ref IntPtr phEnum, uint cl,
        [MarshalAs(UnmanagedType.LPWStr)] string szName,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 4)] uint[] rMembers,
        uint cMax, out uint pcTokens);

    // vtable slot 15: EnumMethods
    void EnumMethods(ref IntPtr phEnum, uint cl,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] uint[] rMethods,
        uint cMax, out uint pcTokens);

    // vtable slot 16: EnumMethodsWithName
    void EnumMethodsWithName(ref IntPtr phEnum, uint cl,
        [MarshalAs(UnmanagedType.LPWStr)] string szName,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 4)] uint[] rMethods,
        uint cMax, out uint pcTokens);

    // vtable slot 17: EnumFields  <- the one we actually call
    void EnumFields(ref IntPtr phEnum, uint cl,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] uint[] rFields,
        uint cMax, out uint pcTokens);

    // vtable slot 18: EnumFieldsWithName
    void EnumFieldsWithName(ref IntPtr phEnum, uint cl,
        [MarshalAs(UnmanagedType.LPWStr)] string szName,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 4)] uint[] rFields,
        uint cMax, out uint pcTokens);

    // vtable slot 19: EnumParams
    void EnumParams(ref IntPtr phEnum, uint mb,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] uint[] rParams,
        uint cMax, out uint pcTokens);

    // vtable slot 20: EnumMemberRefs
    void EnumMemberRefs(ref IntPtr phEnum, uint tkParent,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] uint[] rMemberRefs,
        uint cMax, out uint pcTokens);

    // vtable slot 21: EnumMethodImpls
    void EnumMethodImpls(ref IntPtr phEnum, uint td,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] uint[] rMethodBody,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] uint[] rMethodDecl,
        uint cMax, out uint pcTokens);

    // vtable slot 22: EnumPermissionSets
    void EnumPermissionSets(ref IntPtr phEnum, uint tk, uint dwActions,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 4)] uint[] rPermission,
        uint cMax, out uint pcTokens);

    // vtable slot 23: FindMember
    void FindMember(uint td,
        [MarshalAs(UnmanagedType.LPWStr)] string szName,
        IntPtr pvSigBlob, uint cbSigBlob, out uint pmb);

    // vtable slot 24: FindMethod
    void FindMethod(uint td,
        [MarshalAs(UnmanagedType.LPWStr)] string szName,
        IntPtr pvSigBlob, uint cbSigBlob, out uint pmb);

    // vtable slot 25: FindField  <- used for named field lookup
    void FindField(uint td,
        [MarshalAs(UnmanagedType.LPWStr)] string szName,
        IntPtr pvSigBlob, uint cbSigBlob, out uint pmb);

    // vtable slot 26: FindMemberRef
    void FindMemberRef(uint td,
        [MarshalAs(UnmanagedType.LPWStr)] string szName,
        IntPtr pvSigBlob, uint cbSigBlob, out uint pmr);

    // vtable slot 27: GetMethodProps
    void GetMethodProps(uint mb, out uint pClass,
        IntPtr szMethod, uint cchMethod, out uint pchMethod,
        out uint pdwAttr, out IntPtr ppvSigBlob, out uint pcbSigBlob,
        out uint pulCodeRVA, out uint pdwImplFlags);

    // vtable slot 28: GetMemberRefProps
    void GetMemberRefProps(uint mr, out uint ptk,
        IntPtr szMember, uint cchMember, out uint pchMember,
        out IntPtr ppvSigBlob, out uint pbSig);

    // vtable slot 29: EnumProperties
    void EnumProperties(ref IntPtr phEnum, uint td,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] uint[] rProperties,
        uint cMax, out uint pcProperties);

    // vtable slot 30: EnumEvents
    void EnumEvents(ref IntPtr phEnum, uint td,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] uint[] rEvents,
        uint cMax, out uint pcEvents);

    // vtable slot 31: GetEventProps
    void GetEventProps(uint ev, out uint pClass,
        IntPtr szEvent, uint cchEvent, out uint pchEvent,
        out uint pdwEventFlags, out uint ptkEventType,
        out uint pmdAddOn, out uint pmdRemoveOn, out uint pmdFire,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 11)] uint[] rmdOtherMethod,
        uint cMax, out uint pcOtherMethod);

    // vtable slot 32: EnumMethodSemantics
    void EnumMethodSemantics(ref IntPtr phEnum, uint mb,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] uint[] rEventProp,
        uint cMax, out uint pcEventProp);

    // vtable slot 33: GetMethodSemantics
    void GetMethodSemantics(uint mb, uint tkEventProp, out uint pdwSemanticsFlags);

    // vtable slot 34: GetClassLayout
    void GetClassLayout(uint td, out uint pdwPackSize,
        IntPtr rFieldOffset, uint cMax, out uint pcFieldOffset, out uint pulClassSize);

    // vtable slot 35: GetFieldMarshal
    void GetFieldMarshal(uint tk, out IntPtr ppvNativeType, out uint pcbNativeType);

    // vtable slot 36: GetRVA
    void GetRVA(uint tk, out uint pulCodeRVA, out uint pdwImplFlags);

    // vtable slot 37: GetPermissionSetProps
    void GetPermissionSetProps(uint pm, out uint pdwAction,
        out IntPtr ppvPermission, out uint pcbPermission);

    // vtable slot 38: GetSigFromToken
    void GetSigFromToken(uint mdSig, out IntPtr ppvSig, out uint pcbSig);

    // vtable slot 39: GetModuleRefProps
    void GetModuleRefProps(uint mur, IntPtr szName, uint cchName, out uint pchName);

    // vtable slot 40: EnumModuleRefs
    void EnumModuleRefs(ref IntPtr phEnum,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] uint[] rModuleRefs,
        uint cMax, out uint pcModuleRefs);

    // vtable slot 41: GetTypeSpecFromToken
    void GetTypeSpecFromToken(uint typespec, out IntPtr ppvSig, out uint pcbSig);

    // vtable slot 42: GetNameFromToken
    void GetNameFromToken(uint tk, out IntPtr pszUtf8NamePtr);

    // vtable slot 43: EnumUnresolvedMethods
    void EnumUnresolvedMethods(ref IntPtr phEnum,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] uint[] rMethods,
        uint cMax, out uint pcTokens);

    // vtable slot 44: GetUserString
    void GetUserString(uint stk, IntPtr szString, uint cchString, out uint pchString);

    // vtable slot 45: GetPinvokeMap
    void GetPinvokeMap(uint tk, out uint pdwMappingFlags,
        IntPtr szImportName, uint cchImportName, out uint pchImportName,
        out uint pmrImportDLL);

    // vtable slot 46: EnumSignatures
    void EnumSignatures(ref IntPtr phEnum,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] uint[] rSignatures,
        uint cMax, out uint pcSignatures);

    // vtable slot 47: EnumTypeSpecs
    void EnumTypeSpecs(ref IntPtr phEnum,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] uint[] rTypeSpecs,
        uint cMax, out uint pcTypeSpecs);

    // vtable slot 48: EnumUserStrings
    void EnumUserStrings(ref IntPtr phEnum,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] uint[] rStrings,
        uint cMax, out uint pcStrings);

    // vtable slot 49: GetParamForMethodIndex
    void GetParamForMethodIndex(uint md, uint ulParamSeq, out uint ppd);

    // vtable slot 50: EnumCustomAttributes
    void EnumCustomAttributes(ref IntPtr phEnum, uint tk, uint tkType,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 4)] uint[] rCustomAttributes,
        uint cMax, out uint pcCustomAttributes);

    // vtable slot 51: GetCustomAttributeProps
    void GetCustomAttributeProps(uint cv, out uint ptkObj, out uint ptkType,
        out IntPtr ppBlob, out uint pcbSize);

    // vtable slot 52: FindTypeRef
    void FindTypeRef(uint tkResolutionScope,
        [MarshalAs(UnmanagedType.LPWStr)] string szName, out uint ptr);

    // vtable slot 53: GetMemberProps  <- useful for GetFieldProps fallback
    void GetMemberProps(uint mb, out uint pClass,
        IntPtr szMember, uint cchMember, out uint pchMember,
        out uint pdwAttr, out IntPtr ppvSigBlob, out uint pcbSigBlob,
        out uint pulCodeRVA, out uint pdwImplFlags,
        out uint pdwCPlusTypeFlag, out IntPtr ppValue, out uint pcchValue);

    // vtable slot 54: GetFieldProps  <- the one we actually call for field names
    void GetFieldProps(uint mb, out uint pClass,
        IntPtr szField, uint cchField, out uint pchField,
        out uint pdwAttr, out IntPtr ppvSigBlob, out uint pcbSigBlob,
        out uint pdwCPlusTypeFlag, out IntPtr ppValue, out uint pcchValue);

    // vtable slot 55: GetPropertyProps
    void GetPropertyProps(uint prop, out uint pClass,
        IntPtr szProperty, uint cchProperty, out uint pchProperty,
        out uint pdwPropFlags, out IntPtr ppvSig, out uint pbSig,
        out uint pdwCPlusTypeFlag, out IntPtr ppDefaultValue, out uint pcchDefaultValue,
        out uint pmdSetter, out uint pmdGetter,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 14)] uint[] rmdOtherMethod,
        uint cMax, out uint pcOtherMethod);

    // vtable slot 56: GetParamProps
    void GetParamProps(uint tk, out uint pmd, out uint pulSequence,
        IntPtr szName, uint cchName, out uint pchName,
        out uint pdwAttr, out uint pdwCPlusTypeFlag,
        out IntPtr ppValue, out uint pcchValue);

    // vtable slot 57: GetCustomAttributeByName
    void GetCustomAttributeByName(uint tkObj,
        [MarshalAs(UnmanagedType.LPWStr)] string szName,
        out IntPtr ppData, out uint pcbData);

    // vtable slot 58: IsValidToken
    [return: MarshalAs(UnmanagedType.Bool)]
    bool IsValidToken(uint tk);

    // vtable slot 59: GetNestedClassProps
    void GetNestedClassProps(uint tdNestedClass, out uint ptdEnclosingClass);

    // vtable slot 60: GetNativeCallConvFromSig
    void GetNativeCallConvFromSig(IntPtr pvSig, uint cbSig, out uint pCallConv);

    // vtable slot 61: IsGlobal
    void IsGlobal(uint pd, out int pbGlobal);
}
