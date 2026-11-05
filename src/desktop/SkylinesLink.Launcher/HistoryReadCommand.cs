using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using SkylinesLink.Deployment;
using SkylinesLink.Deployment.Generated;

namespace SkylinesLink.Launcher;

/// <summary>单次只读命令；不初始化 Console、不接同步标准句柄、不重新选 Core。</summary>
internal static class HistoryReadCommand
{
    private const int MaximumRequestBytes = NativeHistoryLimits.MaximumRequestBytes;
    private const int DeadlineMilliseconds = 5000;

    internal static async Task<int> RunAsync(int version = 1)
    {
        int maximumResponseBytes = version switch
        {
            1 => NativeHistoryLimits.Version1MaximumResponseBytes,
            2 => NativeHistoryLimits.Version2MaximumResponseBytes,
            3 => NativeHistoryLimits.Version3MaximumResponseBytes,
            _ => 0
        };
        if (maximumResponseBytes == 0) return (int)HistoryReadCode.InvalidRequest;
        using var deadline = new CancellationTokenSource(DeadlineMilliseconds);
        byte[] inputBytes = new byte[MaximumRequestBytes + 1];
        byte[] outputBytes = new byte[maximumResponseBytes];
        try
        {
            IntPtr rawInput = Pipe(-10), rawOutput = Pipe(-11);
            if (rawInput == rawOutput) return (int)HistoryReadCode.InvalidRequest;
            using var inputHandle = new SafeFileHandle(rawInput, ownsHandle: true);
            using var outputHandle = new SafeFileHandle(rawOutput, ownsHandle: true);
            if (!inputHandle.IsAsync || !outputHandle.IsAsync) return (int)HistoryReadCode.InvalidRequest;
            await using var input = new FileStream(inputHandle, FileAccess.Read, 1, isAsync: true);
            await using var output = new FileStream(outputHandle, FileAccess.Write, 1, isAsync: true);
            int count = 0;
            while (true)
            {
                int read = await input.ReadAsync(inputBytes.AsMemory(count), deadline.Token).ConfigureAwait(false);
                if (read == 0) break;
                count += read;
                if (count > MaximumRequestBytes) return (int)HistoryReadCode.InvalidRequest;
            }
            // No environment lease is acquired until complete bounded JSON and EOF have arrived.
            NativeHistoryRequest? request = null;
            NativeHistoryRequestV2? requestV2 = null;
            NativeHistoryRequestV3? requestV3 = null;
            try
            {
                string json = new UTF8Encoding(false, true).GetString(inputBytes, 0, count);
                if (version == 3) requestV3 = HistoryReader.ParseRequestV3(json);
                else if (version == 2) requestV2 = HistoryReader.ParseRequestV2(json);
                else request = HistoryReader.ParseRequest(json);
            }
            catch (Exception error) when (error is ArgumentException or FormatException or InvalidDataException or JsonException or HistoryReadException)
            { return (int)HistoryReadCode.InvalidRequest; }
            Array.Clear(inputBytes);
            NativeHistoryCapture? capture = null;
            NativeHistoryCaptureV2? captureV2 = null;
            NativeHistoryCaptureV3? captureV3 = null;
            if (version == 3) captureV3 = await HistoryReader.CaptureV3Async(requestV3!, deadline.Token).ConfigureAwait(false);
            else if (version == 2) captureV2 = await HistoryReader.CaptureV2Async(requestV2!, deadline.Token).ConfigureAwait(false);
            else capture = await HistoryReader.CaptureAsync(request!, deadline.Token).ConfigureAwait(false);
            // Capture owns no complete file/DOM, and its authority lease is already released here.
            int outputCount;
            using (var bounded = new MemoryStream(outputBytes, 0, outputBytes.Length, writable: true, publiclyVisible: false))
            {
                using (var writer = new Utf8JsonWriter(bounded))
                {
                    if (version == 3) DeploymentContract.WriteNativeHistoryCaptureV3(writer, captureV3!);
                    else if (version == 2) DeploymentContract.WriteNativeHistoryCaptureV2(writer, captureV2!);
                    else DeploymentContract.WriteNativeHistoryCapture(writer, capture!);
                    writer.Flush();
                }
                outputCount = checked((int)bounded.Position);
            }
            deadline.Token.ThrowIfCancellationRequested();
            await output.WriteAsync(outputBytes.AsMemory(0, outputCount), deadline.Token).ConfigureAwait(false);
            return 0;
        }
        catch (HistoryReadException error) { return (int)error.Code; }
        catch (Exception) { return (int)HistoryReadCode.IoFailed; }
        finally { Array.Clear(inputBytes); Array.Clear(outputBytes); }
    }

    private static IntPtr Pipe(int id)
    {
        IntPtr handle = GetStdHandle(id);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1) || GetFileType(handle) != 3)
            throw new HistoryReadException(HistoryReadCode.InvalidRequest);
        return handle;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int id);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetFileType(IntPtr handle);
}
