using System.Diagnostics;
using System.Globalization;

namespace Offloadr.Runner.Tests;

/// <summary>A throwaway Linux user for tests that need a second, unprivileged uid. Needs root.</summary>
internal sealed class LinuxTestUser : IAsyncDisposable
{
    private LinuxTestUser(string name, uint userId)
    {
        Name = name;
        UserId = userId;
    }

    public string Name { get; }
    public uint UserId { get; }

    public static async Task<LinuxTestUser> CreateAsync()
    {
        LinuxTestPrerequisites.RequireRoot();
        LinuxTestPrerequisites.RequireCommand("/usr/sbin/useradd");
        LinuxTestPrerequisites.RequireCommand("/usr/sbin/userdel");

        var name = $"rrt_{Guid.NewGuid():N}"[..16];
        var runner = new LinuxCommandRunner();
        await runner.RunAsync(
            new LinuxCommand("/usr/sbin/useradd", ["--no-create-home", "--shell", "/usr/sbin/nologin", name]),
            CancellationToken.None);
        var id = await runner.RunAsync(LinuxCommandFactory.CheckUserExists(name), CancellationToken.None);
        return new LinuxTestUser(name, uint.Parse(id.Stdout.Trim(), CultureInfo.InvariantCulture));
    }

    public async ValueTask DisposeAsync()
    {
        await LinuxProcessReaper.KillAllOwnedByAsync(UserId, CancellationToken.None);
        await new LinuxCommandRunner().RunAsync(
            new LinuxCommand("/usr/sbin/userdel", ["--force", Name], ThrowOnError: false),
            CancellationToken.None);
    }
}

/// <summary>
/// A minimal native VFS IPC client, so requests can come from a process running as another uid.
/// Prints "status=N lease=N" for the single response it reads.
/// </summary>
internal static class NativeVfsIpcClient
{
    public static async Task<string> CompileAsync(string directory)
    {
        LinuxTestPrerequisites.RequireCommand("/usr/bin/gcc");
        var sourcePath = Path.Combine(directory, "vfs-ipc-client.c");
        var binaryPath = Path.Combine(directory, "vfs-ipc-client");
        await File.WriteAllTextAsync(sourcePath, Source);
        await VfsShimLinuxTests.RunProcessAsync("/usr/bin/gcc", [sourcePath, "-o", binaryPath], directory);
        return binaryPath;
    }

    public static async Task<(byte Status, ulong LeaseId)> RunAsync(
        string binaryPath,
        string? userName,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = binaryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = "/"
        };
        if (!string.IsNullOrEmpty(userName))
        {
            startInfo.UserName = userName;
        }

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start the IPC client.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var output = (await stdout).Trim();
        if (process.ExitCode != 0)
        {
            Assert.Fail($"IPC client exited with {process.ExitCode}: {output} {(await stderr).Trim()}");
        }

        var fields = output.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(static field => field.Split('=', 2))
            .ToDictionary(static pair => pair[0], static pair => pair[1], StringComparer.Ordinal);
        return (
            byte.Parse(fields["status"], CultureInfo.InvariantCulture),
            ulong.Parse(fields["lease"], CultureInfo.InvariantCulture));
    }

    private const string Source = """
        #define _GNU_SOURCE
        #include <endian.h>
        #include <stdint.h>
        #include <stdio.h>
        #include <stdlib.h>
        #include <string.h>
        #include <sys/socket.h>
        #include <sys/un.h>
        #include <unistd.h>

        static void put32(uint8_t *buffer, uint32_t value) { value = htobe32(value); memcpy(buffer, &value, 4); }
        static void put64(uint8_t *buffer, uint64_t value) { value = htobe64(value); memcpy(buffer, &value, 8); }

        int main(int argc, char **argv) {
            if (argc < 4) return 10;
            uint8_t payload[4096];
            uint32_t length = 0;
            uint8_t operation = 0;
            if (strcmp(argv[2], "open") == 0 && argc == 5) {
                size_t path_length = strlen(argv[3]);
                size_t session_length = strlen(argv[4]);
                if (path_length + session_length + 9 > sizeof(payload)) return 11;
                operation = 1;
                put32(payload, (uint32_t)path_length);
                put32(payload + 4, (uint32_t)session_length);
                payload[8] = 0;
                memcpy(payload + 9, argv[3], path_length);
                memcpy(payload + 9 + path_length, argv[4], session_length);
                length = (uint32_t)(9 + path_length + session_length);
            } else if (strcmp(argv[2], "range") == 0 && argc == 5) {
                operation = 2;
                put64(payload, strtoull(argv[3], NULL, 10));
                put64(payload + 8, strtoull(argv[4], NULL, 10));
                put64(payload + 16, 0);
                put64(payload + 24, 4);
                length = 32;
            } else if ((strcmp(argv[2], "release") == 0 || strcmp(argv[2], "ack") == 0) && argc == 4) {
                operation = strcmp(argv[2], "release") == 0 ? 4 : 5;
                put64(payload, strtoull(argv[3], NULL, 10));
                length = 8;
            } else {
                return 12;
            }

            int fd = socket(AF_UNIX, SOCK_STREAM, 0);
            if (fd < 0) return 13;
            struct sockaddr_un address;
            memset(&address, 0, sizeof(address));
            address.sun_family = AF_UNIX;
            snprintf(address.sun_path, sizeof(address.sun_path), "%s", argv[1]);
            if (connect(fd, (struct sockaddr *)&address, sizeof(address)) != 0) return 14;

            uint8_t header[12] = {0};
            put32(header, 0x4f564653);
            header[4] = 3;
            header[5] = operation;
            put32(header + 8, length);
            if (write(fd, header, sizeof(header)) != (ssize_t)sizeof(header)) return 15;
            if (write(fd, payload, length) != (ssize_t)length) return 16;

            uint8_t response[52];
            size_t received = 0;
            while (received < sizeof(response)) {
                ssize_t result = read(fd, response + received, sizeof(response) - received);
                if (result <= 0) return 17;
                received += (size_t)result;
            }
            close(fd);

            uint64_t lease;
            memcpy(&lease, response + 12, 8);
            printf("status=%u lease=%llu\n", response[5], (unsigned long long)be64toh(lease));
            return 0;
        }
        """;
}
