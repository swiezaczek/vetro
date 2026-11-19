using System;
using System.Runtime.InteropServices;

namespace Launcher.Native
{
    /// <summary>
    /// The few libc calls the launcher needs. Environment.SetEnvironmentVariable only changes
    /// the managed copy on Unix, so EFL (which calls getenv) never sees it; setenv does.
    /// </summary>
    internal static class Libc
    {
        private const string C = "libc.so.6";
        public const int O_RDONLY = 0;
        public const int O_WRONLY = 1;
        public const int O_RDWR = 2;
        public const int O_NONBLOCK = 0x800;   // Linux (ARM and x86)
        public const int O_CLOEXEC = 0x80000;
        public const int F_GETFL = 3;
        public const int F_SETFL = 4;
        public const int EAGAIN = 11;
        public const int EINTR = 4;
        public const int EBUSY = 16;

        public const short POLLIN = 1;
        public const short POLLERR = 8;
        public const short POLLHUP = 16;
        public const short POLLNVAL = 32;

        public const int IN_NONBLOCK = 0x800;
        public const uint IN_ATTRIB = 0x00000004u;
        public const uint IN_CREATE = 0x00000100u;
        public const uint IN_DELETE = 0x00000200u;

        public const uint EVIOCGRAB = 0x40044590u;
        public const uint UI_SET_EVBIT = 0x40045564u;
        public const uint UI_SET_KEYBIT = 0x40045565u;
        public const uint UI_DEV_CREATE = 0x00005501u;
        public const uint UI_DEV_DESTROY = 0x00005502u;
        public const uint UI_SET_PHYS_32 = 0x4004556cu;
        public const uint UI_SET_PHYS_64 = 0x4008556cu;

        [StructLayout(LayoutKind.Sequential)]
        public struct PollFd
        {
            public int fd;
            public short events;
            public short revents;
        }

        [DllImport(C, SetLastError = true)] public static extern int setenv(string name, string value, int overwrite);
        [DllImport(C, SetLastError = true)] public static extern int mkfifo(string path, uint mode);
        [DllImport(C, SetLastError = true)] public static extern int open(string path, int flags);
        [DllImport(C, SetLastError = true)] public static extern int close(int fd);
        [DllImport(C, SetLastError = true)] public static extern IntPtr read(int fd, byte[] buf, UIntPtr count);
        [DllImport(C, SetLastError = true)] public static extern IntPtr write(int fd, byte[] buf, UIntPtr count);
        [DllImport(C, SetLastError = true)] public static extern int pipe(int[] pipefd);
        [DllImport(C, SetLastError = true)] public static extern int fcntl(int fd, int cmd, int arg);
        [DllImport(C, SetLastError = true)] public static extern int poll([In, Out] PollFd[] fds, UIntPtr nfds, int timeout);
        [DllImport(C, SetLastError = true, EntryPoint = "ioctl")] public static extern int ioctl_int(int fd, UIntPtr request, int value);
        [DllImport(C, SetLastError = true, EntryPoint = "ioctl")] public static extern int ioctl_void(int fd, UIntPtr request);
        [DllImport(C, SetLastError = true, EntryPoint = "ioctl", CharSet = CharSet.Ansi)] public static extern int ioctl_str(int fd, UIntPtr request, string value);
        [DllImport(C, SetLastError = true)] public static extern int inotify_init1(int flags);
        [DllImport(C, SetLastError = true)] public static extern int inotify_add_watch(int fd, string pathname, uint mask);
        [DllImport(C)] public static extern uint getuid();
        [DllImport(C)] public static extern int getpid();
        [DllImport(C, SetLastError = true)] public static extern int unlink(string path);
        [DllImport(C, SetLastError = true)] public static extern int kill(int pid, int sig);
        [DllImport(C, SetLastError = true)] public static extern int chmod(string path, uint mode);
        [DllImport(C)] public static extern int system(string command);
        [DllImport(C)] public static extern void free(IntPtr ptr);
    }
}
