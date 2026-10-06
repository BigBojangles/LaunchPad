using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Security.AccessControl;

namespace LaunchPad.Services.Fence;

// Windows adapter. These persistent filters affect outbound connections from
// the exact QEMU application path, for every identity using that executable.
// They neither enumerate fixed host addresses nor change hostwide firewall modes.
[SupportedOSPlatform("windows")]
public static class WindowsHostNetworkBoundary
{
    private static readonly Guid SubLayerKey = new("0ea53387-3f11-4e97-9677-5b47af29bdad");
    private static readonly Guid ConnectV4 = new("c38d57d1-05a7-4c33-904f-7fbceee60e82");
    private static readonly Guid ConnectV6 = new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");
    private static readonly Guid AppIdKey = new("d78e1e87-8644-4ea5-9437-d809ecefc971");
    private static readonly Guid FlagsKey = new("632ce23b-5167-435c-86d7-e903684aa80c");
    private const uint Persistent = 1, Block = 0x1001, Loopback = 1;
    private const string Name = "LaunchPad QEMU host boundary";
    public const string PreparationArgument = "--prepare-host-network";

    public static bool IsPreparationRequest(string[] arguments) =>
        arguments.Length == 2 && arguments[0] == PreparationArgument;

    // Explicit administrative setup, not an implicit elevation at every launch.
    // A transaction publishes both families together. Existing matching rules
    // remain in place; mismatched objects are refused rather than overwritten.
    public static void PrepareRuntime(string runtimeRoot)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new Win32Exception(5, "Host-network setup requires administrator access once.");
        var root = Path.GetFullPath(runtimeRoot);
        var paths = new[] { Path.Combine(root, "qemu", "fence", "qemu-system-x86_64.exe"),
            Path.Combine(root, "qemu", "qemu-system-x86_64.exe") }.Where(File.Exists).ToArray();
        if (paths.Length == 0) throw new FileNotFoundException("No packaged QEMU executable was found.");
        foreach (var path in paths) ValidatePath(path);
        using var engine = Open();
        using var descriptor = new RuleSecurity();
        Check(FwpmTransactionBegin0(engine.Handle, 0), "Begin host-network setup");
        var committed = false;
        try
        {
            var result = FwpmSubLayerGetByKey0(engine.Handle, SubLayerKey, out var pointer);
            if (result == 0)
            {
                try { ValidateSubLayer(Marshal.PtrToStructure<SubLayer>(pointer)); }
                finally { FwpmFreeMemory0(ref pointer); }
                ValidateSecurity(engine.Handle, SubLayerKey, false);
            }
            else if (result == 0x80320007) // FWP_E_SUBLAYER_NOT_FOUND
            {
                var layer = new SubLayer { Key = SubLayerKey, Display = new Display { Name = Name },
                    Flags = Persistent, Weight = ushort.MaxValue };
                Check(FwpmSubLayerAdd0(engine.Handle, ref layer, descriptor.Pointer), "Add LaunchPad sublayer");
            }
            else Check(result, "Read LaunchPad sublayer");
            foreach (var path in paths)
            {
                var app = ApplicationId(path);
                foreach (var layer in new[] { ConnectV4, ConnectV6 })
                {
                    var key = RuleKey(app, layer);
                    result = FwpmFilterGetByKey0(engine.Handle, key, out pointer);
                    if (result == 0)
                    {
                        try { ReadAndValidate(pointer, app, layer, key); }
                        finally { FwpmFreeMemory0(ref pointer); }
                        ValidateSecurity(engine.Handle, key, true);
                    }
                    else if (result == 0x80320003) Add(engine.Handle, app, layer, key, descriptor.Pointer);
                    else Check(result, "Read host-network filter");
                }
            }
            Check(FwpmTransactionCommit0(engine.Handle), "Commit host-network setup");
            committed = true;
        }
        finally { if (!committed) FwpmTransactionAbort0(engine.Handle); }
        foreach (var path in paths) Require(path);
    }

    // Read the actual engine objects, not a receipt/registry flag. No missing,
    // disabled, wrong-path, one-family or nonpersistent policy is accepted.
    public static void Require(string executable)
    {
        ValidatePath(executable);
        var app = ApplicationId(executable);
        using var engine = Open();
        Check(FwpmSubLayerGetByKey0(engine.Handle, SubLayerKey, out var pointer), "Read LaunchPad network sublayer");
        try { ValidateSubLayer(Marshal.PtrToStructure<SubLayer>(pointer)); }
        finally { FwpmFreeMemory0(ref pointer); }
        ValidateSecurity(engine.Handle, SubLayerKey, false);
        foreach (var layer in new[] { ConnectV4, ConnectV6 })
        {
            var key = RuleKey(app, layer);
            Check(FwpmFilterGetByKey0(engine.Handle, key, out pointer), "Host-network protection is not prepared");
            try { ReadAndValidate(pointer, app, layer, key); }
            finally { FwpmFreeMemory0(ref pointer); }
            ValidateSecurity(engine.Handle, key, true);
        }
    }

    private static void ValidatePath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)
            || !Path.GetFileName(path).Equals("qemu-system-x86_64.exe", StringComparison.OrdinalIgnoreCase)
            || !FenceFiles.TryResolveUnlinked(Path.GetDirectoryName(path)!, Path.GetFileName(path), out _))
            throw new InvalidDataException("Host-network protection requires an existing unlinked QEMU executable.");
    }

    private static void ValidateSubLayer(SubLayer layer)
    {
        if (layer.Key != SubLayerKey || layer.Flags != Persistent || layer.Provider != 0
            || layer.ProviderData.Size != 0 || layer.Weight != ushort.MaxValue || layer.Display.Name != Name)
            throw new InvalidDataException("The LaunchPad network sublayer does not match its required policy.");
    }

    private static Guid RuleKey(byte[] app, Guid layer)
    {
        // WFP's app ID is a canonical NT-device-path blob, not an executable hash.
        var bytes = SHA256.HashData(app.Concat(layer.ToByteArray()).Concat(SubLayerKey.ToByteArray()).ToArray());
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static byte[] ApplicationId(string path)
    {
        Check(FwpmGetAppIdFromFileName0(path, out var pointer), "Resolve QEMU application identity");
        try { return BlobBytes(Marshal.PtrToStructure<Blob>(pointer)); }
        finally { FwpmFreeMemory0(ref pointer); }
    }

    private static byte[] BlobBytes(Blob blob)
    {
        if (blob.Size == 0 || blob.Size > 131072 || blob.Data == 0)
            throw new InvalidDataException("Invalid network application identity.");
        var bytes = new byte[(int)blob.Size];
        Marshal.Copy(blob.Data, bytes, 0, bytes.Length);
        return bytes;
    }

    private static void ReadAndValidate(nint pointer, byte[] expectedApp, Guid expectedLayer, Guid expectedKey)
    {
        var filter = Marshal.PtrToStructure<Filter>(pointer);
        if (filter.Key != expectedKey || filter.Flags != Persistent || filter.Provider != 0
            || filter.Layer != expectedLayer || filter.SubLayer != SubLayerKey
            || filter.Weight.Type != 1 || filter.Weight.Byte != 15
            || filter.ProviderData.Size != 0 || filter.Context.ProviderKey != Guid.Empty || filter.Reserved != 0
            || filter.Action.Type != Block || filter.Action.Key != Guid.Empty
            || filter.ConditionCount != 2 || filter.Conditions == 0 || filter.Display.Name != Name)
            throw new InvalidDataException("The QEMU host-network filter does not match its required policy.");
        var conditions = Enumerable.Range(0, 2).Select(index =>
            Marshal.PtrToStructure<Condition>(filter.Conditions + index * Marshal.SizeOf<Condition>())).ToArray();
        var application = conditions.SingleOrDefault(value => value.Key == AppIdKey);
        var flags = conditions.SingleOrDefault(value => value.Key == FlagsKey);
        if (application.Key != AppIdKey || application.Match != 0 || application.Value.Type != 12
            || application.Value.Pointer == 0 || flags.Key != FlagsKey || flags.Match != 6
            || flags.Value.Type != 3 || flags.Value.Number != Loopback
            || !BlobBytes(Marshal.PtrToStructure<Blob>(application.Value.Pointer)).SequenceEqual(expectedApp))
            throw new InvalidDataException("QEMU host-network filter conditions have changed.");
    }

    private static void ValidateSecurity(nint engine, Guid key, bool filter)
    {
        nint owner, group, dacl, sacl, descriptor;
        var result = filter
            ? FwpmFilterGetSecurityInfoByKey0(engine, key, 5, out owner, out group, out dacl, out sacl, out descriptor)
            : FwpmSubLayerGetSecurityInfoByKey0(engine, key, 5, out owner, out group, out dacl, out sacl, out descriptor);
        Check(result, "Read actual network-policy permissions");
        try
        {
            if (owner == 0 || dacl == 0 || descriptor == 0
                || new SecurityIdentifier(owner).Value != "S-1-5-32-544"
                || !GetSecurityDescriptorControl(descriptor, out var control, out _) || (control & 0x1000) == 0)
                throw new InvalidDataException("Network-policy ownership or protected permissions have changed.");
            var length = unchecked((ushort)Marshal.ReadInt16(dacl, 2));
            if (length < 8) throw new InvalidDataException("Invalid network-policy ACL.");
            var bytes = new byte[length];
            Marshal.Copy(dacl, bytes, 0, length);
            var acl = new RawAcl(bytes, 0);
            var expected = new Dictionary<string, int[]>
            {
                ["S-1-5-18"] = [0x10000000, 0x000f07ff],
                ["S-1-5-32-544"] = [0x10000000, 0x000f07ff],
                ["S-1-1-0"] = [unchecked((int)0x80000000), 0x000201b4]
            };
            foreach (GenericAce ace in acl)
            {
                if (ace is not CommonAce common || common.IsCallback || common.AceFlags != AceFlags.None
                    || common.AceQualifier != AceQualifier.AccessAllowed
                    || !expected.Remove(common.SecurityIdentifier.Value, out var rights) || !rights.Contains(common.AccessMask))
                    throw new InvalidDataException("Network-policy ACL grants unexpected rights.");
            }
            if (expected.Count != 0) throw new InvalidDataException("Network-policy ACL lacks required protected access.");
        }
        finally { if (descriptor != 0) FwpmFreeMemory0(ref descriptor); }
    }

    private static void Add(nint engine, byte[] app, Guid layer, Guid key, nint descriptor)
    {
        var appBytes = Marshal.AllocHGlobal(app.Length);
        var appBlob = Marshal.AllocHGlobal(Marshal.SizeOf<Blob>());
        var conditions = Marshal.AllocHGlobal(2 * Marshal.SizeOf<Condition>());
        try
        {
            Marshal.Copy(app, 0, appBytes, app.Length);
            Marshal.StructureToPtr(new Blob { Size = (uint)app.Length, Data = appBytes }, appBlob, false);
            Marshal.StructureToPtr(new Condition { Key = AppIdKey, Match = 0,
                Value = new Value { Type = 12, Pointer = appBlob } }, conditions, false);
            Marshal.StructureToPtr(new Condition { Key = FlagsKey, Match = 6,
                Value = new Value { Type = 3, Number = Loopback } }, conditions + Marshal.SizeOf<Condition>(), false);
            var filter = new Filter { Key = key, Display = new Display { Name = Name }, Flags = Persistent,
                Layer = layer, SubLayer = SubLayerKey, Weight = new Value { Type = 1, Byte = 15 },
                ConditionCount = 2, Conditions = conditions, Action = new Action { Type = Block } };
            Check(FwpmFilterAdd0(engine, ref filter, descriptor, out _), "Add scoped QEMU host-network filter");
        }
        finally { Marshal.FreeHGlobal(conditions); Marshal.FreeHGlobal(appBlob); Marshal.FreeHGlobal(appBytes); }
    }

    private static Engine Open()
    {
        // SDK ABI: x64 FWPM_FILTER0=200, CONDITION0=40, SUBLAYER0=72.
        if (IntPtr.Size != 8 || Marshal.SizeOf<Filter>() != 200
            || Marshal.SizeOf<Condition>() != 40 || Marshal.SizeOf<SubLayer>() != 72)
            throw new PlatformNotSupportedException("Unsupported Windows filtering ABI.");
        Check(FwpmEngineOpen0(null, 10, 0, 0, out var handle), "Open Windows filtering engine");
        return new Engine(handle);
    }

    private static void Check(uint error, string operation)
    {
        if (error != 0) throw new Win32Exception(unchecked((int)error), operation + ". Fenced launch must remain stopped.");
    }

    private sealed class Engine(nint handle) : IDisposable
    {
        public nint Handle { get; } = handle;
        public void Dispose() => FwpmEngineClose0(Handle);
    }

    private sealed class RuleSecurity : IDisposable
    {
        public nint Pointer { get; }
        public RuleSecurity()
        {
            // Ordinary launches can READ the objects but cannot change/delete
            // policy or link an overriding filter into this private sublayer.
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                "O:BAG:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GR;;;WD)", 1, out var pointer, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            Pointer = pointer;
        }
        public void Dispose() => LocalFree(Pointer);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Display { [MarshalAs(UnmanagedType.LPWStr)] public string? Name; [MarshalAs(UnmanagedType.LPWStr)] public string? Description; }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public uint Size; public nint Data; }
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct Value { [FieldOffset(0)] public uint Type; [FieldOffset(8)] public byte Byte; [FieldOffset(8)] public uint Number; [FieldOffset(8)] public nint Pointer; }
    [StructLayout(LayoutKind.Sequential)] private struct Condition { public Guid Key; public uint Match; public Value Value; }
    [StructLayout(LayoutKind.Sequential)] private struct Action { public uint Type; public Guid Key; }
    [StructLayout(LayoutKind.Explicit, Size = 16)] private struct Context { [FieldOffset(0)] public ulong Raw; [FieldOffset(0)] public Guid ProviderKey; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Filter
    {
        public Guid Key; public Display Display; public uint Flags; public nint Provider; public Blob ProviderData;
        public Guid Layer, SubLayer; public Value Weight; public uint ConditionCount; public nint Conditions;
        public Action Action; public Context Context; public nint Reserved; public ulong Id; public Value EffectiveWeight;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct SubLayer { public Guid Key; public Display Display; public uint Flags; public nint Provider; public Blob ProviderData; public ushort Weight; }

    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint FwpmEngineOpen0(string? server, uint authentication, nint identity, nint session, out nint handle);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)] private static extern uint FwpmEngineClose0(nint handle);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)] private static extern uint FwpmTransactionBegin0(nint handle, uint flags);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)] private static extern uint FwpmTransactionCommit0(nint handle);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)] private static extern uint FwpmTransactionAbort0(nint handle);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)] private static extern uint FwpmSubLayerGetByKey0(nint handle, in Guid key, out nint layer);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)] private static extern uint FwpmSubLayerAdd0(nint handle, ref SubLayer layer, nint descriptor);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)] private static extern uint FwpmFilterGetByKey0(nint handle, in Guid key, out nint filter);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)] private static extern uint FwpmFilterAdd0(nint handle, ref Filter filter, nint descriptor, out ulong id);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)] private static extern uint FwpmFilterGetSecurityInfoByKey0(nint handle, in Guid key, uint information, out nint owner, out nint group, out nint dacl, out nint sacl, out nint descriptor);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)] private static extern uint FwpmSubLayerGetSecurityInfoByKey0(nint handle, in Guid key, uint information, out nint owner, out nint group, out nint dacl, out nint sacl, out nint descriptor);
    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern uint FwpmGetAppIdFromFileName0(string file, out nint appId);
    [DllImport("fwpuclnt.dll", ExactSpelling = true)] private static extern void FwpmFreeMemory0(ref nint pointer);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string text, uint revision, out nint descriptor, out uint size);
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern nint LocalFree(nint pointer);
    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)] private static extern bool GetSecurityDescriptorControl(nint descriptor, out ushort control, out uint revision);
}
