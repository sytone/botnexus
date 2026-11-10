using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using BotNexus.Satellite.Client.Core;
using BotNexus.Satellite.Client.SignalR;

namespace BotNexus.Satellite.Client.Windows;

/// <summary>Generic current-user Windows credentials, scoped to normalized endpoint and local satellite profile.</summary>
internal sealed class WindowsCredentialStore(ClientProfile profile) : ICredentialStore
{
    private readonly string _prefix = "BotNexus.Satellite.Client/" + profile.CredentialScope + "/";
    public Task<ClientCredentials> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ClientCredentials { GatewayApiKey = Read("gateway"), TunnelAccessToken = Read("tunnel") });
    }
    public Task SaveTunnelTokenAsync(string token, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write("tunnel", token);
        return Task.CompletedTask;
    }
    public void Save(string? apiKey, string? tunnelToken)
    {
        if (apiKey is not null) Write("gateway", apiKey);
        if (tunnelToken is not null) Write("tunnel", tunnelToken);
    }
    public void Delete()
    {
        Delete("gateway");
        Delete("tunnel");
    }
    private void Delete(string name)
    {
        if (!CredDeleteW(_prefix + name, 1, 0) && Marshal.GetLastWin32Error() != 1168)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    private string? Read(string name)
    {
        if (!CredReadW(_prefix + name, 1, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1168) return null;
            throw new Win32Exception(error);
        }
        byte[]? bytes = null;
        Credential credential = default;
        try
        {
            credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.BlobSize > 2560 || credential.BlobSize % 2 != 0)
                throw new InvalidOperationException("Stored satellite credential has an invalid size.");
            bytes = new byte[credential.BlobSize];
            Marshal.Copy(credential.Blob, bytes, 0, bytes.Length);
            return Encoding.Unicode.GetString(bytes);
        }
        finally
        {
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
            Zero(credential.Blob, credential.BlobSize);
            CredFree(pointer);
        }
    }
    private void Write(string name, string secret)
    {
        if (string.IsNullOrEmpty(secret) || secret.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
            throw new ArgumentException("Credentials must be nonempty and contain no whitespace or control characters.");
        var bytes = Encoding.Unicode.GetBytes(secret);
        nint pointer = 0;
        try
        {
            if (bytes.Length > 2560) throw new ArgumentException("Credential exceeds the Windows credential size limit.");
            pointer = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            var credential = new Credential { Type = 1, TargetName = _prefix + name, BlobSize = (uint)bytes.Length, Blob = pointer, Persist = 2, UserName = "satellite-client" };
            if (!CredWriteW(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            Zero(pointer, (uint)bytes.Length);
            if (pointer != 0) Marshal.FreeHGlobal(pointer);
        }
    }
    private static void Zero(nint pointer, uint size)
    {
        if (pointer == 0) return;
        for (var i = 0; i < size; i++) Marshal.WriteByte(pointer, i, 0);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string? TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public nint Blob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public string? TargetAlias;
        public string? UserName;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(string target, uint type, uint flags, out nint credential);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWriteW(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(string target, uint type, uint flags);
    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern void CredFree(nint pointer);
}
