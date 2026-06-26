using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace InfiniteEnergy
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
            {
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                if (!principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
                {
                    MessageBox.Show("请以管理员身份运行！", "无限能量", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly EnergyController _controller;
        private Button _btnInit;
        private Button _btnEnergy;
        private Label _lblStatus;
        private bool _energyEnabled;
        private bool _initialized;

        public MainForm()
        {
            _controller = new EnergyController();
            _controller.Log += msg =>
            {
                if (InvokeRequired)
                    Invoke(new Action<string>(m => _lblStatus.Text = m), msg);
                else
                    _lblStatus.Text = msg;
            };

            Text = "无限能量";
            Size = new System.Drawing.Size(280, 200);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;

            _btnInit = new Button
            {
                Text = "初始化",
                Size = new System.Drawing.Size(240, 40),
                Location = new System.Drawing.Point(15, 15)
            };
            _btnInit.Click += (s, e) =>
            {
                try
                {
                    _btnInit.Enabled = false;
                    _lblStatus.Text = "正在连接游戏进程...";
                    Refresh();
                    _controller.Initialize();
                    _initialized = true;
                    _btnEnergy.Enabled = true;
                    _lblStatus.Text = "就绪";
                }
                catch (Exception ex)
                {
                    _btnInit.Enabled = true;
                    _lblStatus.Text = ex.Message;
                }
            };
            Controls.Add(_btnInit);

            _btnEnergy = new Button
            {
                Text = "无限能量：关闭",
                Size = new System.Drawing.Size(240, 40),
                Location = new System.Drawing.Point(15, 65),
                Enabled = false
            };
            _btnEnergy.Click += (s, e) =>
            {
                if (!_initialized) return;
                try
                {
                    _controller.Toggle();
                    _energyEnabled = !_energyEnabled;
                    _btnEnergy.Text = _energyEnabled ? "无限能量：开启" : "无限能量：关闭";
                }
                catch (Exception ex)
                {
                    _lblStatus.Text = ex.Message;
                }
            };
            Controls.Add(_btnEnergy);

            _lblStatus = new Label
            {
                Text = "请先点击\"初始化\"",
                AutoSize = false,
                Size = new System.Drawing.Size(240, 30),
                Location = new System.Drawing.Point(15, 115),
                TextAlign = System.Drawing.ContentAlignment.MiddleCenter
            };
            Controls.Add(_lblStatus);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _controller.Dispose();
            base.OnFormClosed(e);
        }
    }

    internal sealed class EnergyController : IDisposable
    {
        private const int ProcessAllAccess = 0x1F0FFF;
        private const uint MemCommit = 0x1000;
        private const uint MemCommitReserve = 0x3000;
        private const uint PageExecuteReadWrite = 0x40;
        private const long MinValidPointer = 0x10000;
        private const uint PageNoAccess = 0x01;
        private const uint PageGuard = 0x100;
        private const uint ReadableProtectMask = 0xEE;
        private const int DefaultCaveSize = 0x1000;
        private const int RelativeJumpLength = 5;

        private static readonly byte[] EnergyPattern = { 0x89, 0x46, 0x64, 0xC5, 0xFA, 0x5C, 0xC1, 0xC5, 0xFA, 0x11, 0x46, 0x30 };
        private const int EnergyHookOffset = 7;
        private const int EnergyHookPatchLength = 5;
        private const uint EnergyMaxValue = 0x41600000;

        private IntPtr _processHandle = IntPtr.Zero;
        private Process _targetProcess;
        private IntPtr _moduleBase = IntPtr.Zero;
        private int _moduleSize;
        private IntPtr _energySite = IntPtr.Zero;
        private IntPtr _energyHookSite = IntPtr.Zero;
        private IntPtr _remoteEnergyCave = IntPtr.Zero;
        private byte[] _energyHookOriginal;
        private bool _energyHookInstalled;

        public event Action<string> Log;

        public void Initialize()
        {
            if (_processHandle != IntPtr.Zero) Reset();

            _targetProcess = Process.GetProcessesByName("Sky").FirstOrDefault();
            if (_targetProcess == null)
                throw new InvalidOperationException("未找到 Sky 进程，请先启动游戏");

            _processHandle = OpenProcess(ProcessAllAccess, false, _targetProcess.Id);
            if (_processHandle == IntPtr.Zero)
                throw new InvalidOperationException("OpenProcess 失败，请以管理员身份运行");

            // 用 Win32 API 获取模块基址（兼容 x86 进程）
            IntPtr[] modules = new IntPtr[1024];
            int cbNeeded;
            if (!EnumProcessModulesEx(_processHandle, modules, modules.Length * IntPtr.Size, out cbNeeded, 0x03))
                throw new InvalidOperationException("EnumProcessModules 失败");
            IntPtr hModule = modules[0];
            MODULEINFO modInfo;
            if (!GetModuleInformation(_processHandle, hModule, out modInfo, (uint)Marshal.SizeOf(typeof(MODULEINFO))))
                throw new InvalidOperationException("GetModuleInformation 失败");
            _moduleBase = modInfo.lpBaseOfDll;
            _moduleSize = (int)modInfo.SizeOfImage;

            _energySite = FindPatternInModule(EnergyPattern);
            if (_energySite == IntPtr.Zero)
                throw new InvalidOperationException("特征码未定位，版本可能不匹配");
            _energyHookSite = new IntPtr(_energySite.ToInt64() + EnergyHookOffset);

            InstallEnergyHook();
            LogLine("已就绪");
        }

        public void Toggle()
        {
            if (_energyHookSite == IntPtr.Zero || _remoteEnergyCave == IntPtr.Zero)
                throw new InvalidOperationException("未初始化");

            if (_energyHookInstalled)
            {
                Patch(_energyHookSite, _energyHookOriginal);
                _energyHookInstalled = false;
                LogLine("已关闭");
            }
            else
            {
                Patch(_energyHookSite, BuildRelativeJumpPatch(_energyHookSite, _remoteEnergyCave, EnergyHookPatchLength));
                _energyHookInstalled = true;
                LogLine("已启用");
            }
        }

        private void InstallEnergyHook()
        {
            if (_energyHookSite == IntPtr.Zero) return;

            _remoteEnergyCave = AllocateNear(_energyHookSite, DefaultCaveSize);
            if (_remoteEnergyCave == IntPtr.Zero)
                throw new InvalidOperationException("远程内存分配失败");

            _energyHookOriginal = ReadBytes(_energyHookSite, EnergyHookPatchLength);

            var cave = new List<byte>();
            cave.Add(0x50);
            cave.Add(0xB8);
            cave.AddRange(BitConverter.GetBytes(EnergyMaxValue));
            cave.AddRange(new byte[] { 0x66, 0x0F, 0x6E, 0xC0 });
            cave.Add(0x58);
            cave.AddRange(_energyHookOriginal);
            cave.AddRange(AbsoluteJump(_energyHookSite.ToInt64() + EnergyHookPatchLength));
            Patch(_remoteEnergyCave, cave.ToArray());
        }

        private byte[] ReadBytes(IntPtr address, int size)
        {
            byte[] buffer = new byte[size];
            IntPtr read;
            if (!ReadProcessMemory(_processHandle, address, buffer, buffer.Length, out read))
                throw new InvalidOperationException("ReadProcessMemory 失败");
            return buffer;
        }

        private void Patch(IntPtr address, byte[] bytes)
        {
            uint oldProtect;
            if (!VirtualProtectEx(_processHandle, address, (UIntPtr)bytes.Length, PageExecuteReadWrite, out oldProtect))
                throw new InvalidOperationException("VirtualProtectEx 失败");
            IntPtr written;
            if (!WriteProcessMemory(_processHandle, address, bytes, bytes.Length, out written))
                throw new InvalidOperationException("WriteProcessMemory 失败");
            uint ignored;
            VirtualProtectEx(_processHandle, address, (UIntPtr)bytes.Length, oldProtect, out ignored);
            FlushInstructionCache(_processHandle, address, (UIntPtr)bytes.Length);
        }

        private IntPtr AllocateNear(IntPtr target, int size)
        {
            const long step = MinValidPointer;
            long targetAddress = target.ToInt64();
            for (long distance = 0; distance < 0x70000000; distance += step)
            {
                long down = targetAddress - distance;
                if (down > MinValidPointer)
                {
                    IntPtr allocated = VirtualAllocEx(_processHandle, new IntPtr(down & ~(step - 1)),
                        (UIntPtr)size, MemCommitReserve, PageExecuteReadWrite);
                    if (allocated != IntPtr.Zero && IsRel32Reachable(target, allocated))
                        return allocated;
                }
                long up = targetAddress + distance;
                IntPtr upAllocated = VirtualAllocEx(_processHandle, new IntPtr(up & ~(step - 1)),
                    (UIntPtr)size, MemCommitReserve, PageExecuteReadWrite);
                if (upAllocated != IntPtr.Zero && IsRel32Reachable(target, upAllocated))
                    return upAllocated;
            }
            return IntPtr.Zero;
        }

        private IntPtr FindPatternInModule(byte[] pattern)
        {
            long moduleStart = _moduleBase.ToInt64();
            long moduleEnd = moduleStart + _moduleSize;
            long extendedStart = moduleStart - 0x10000;
            if (extendedStart < 0) extendedStart = 0;

            foreach (MemoryRegion region in EnumerateReadableRegions(extendedStart, moduleEnd))
            {
                byte[] bytes = TryReadRegion(region);
                if (bytes == null) continue;
                int index = FindPatternOffset(bytes, pattern);
                if (index >= 0) return new IntPtr(region.BaseAddress.ToInt64() + index);
            }

            int searchCount = 0;
            foreach (MemoryRegion region in EnumerateReadableRegions(0, 0x800000000000))
            {
                long regionStart = region.BaseAddress.ToInt64();
                if (regionStart >= extendedStart && regionStart < moduleEnd) continue;
                byte[] bytes = TryReadRegion(region);
                if (bytes == null) continue;
                int index = FindPatternOffset(bytes, pattern);
                if (index >= 0) return new IntPtr(region.BaseAddress.ToInt64() + index);
                if (++searchCount >= 1000) break;
            }
            return IntPtr.Zero;
        }

        private static int FindPatternOffset(byte[] buffer, byte[] pattern)
        {
            for (int i = 0; i <= buffer.Length - pattern.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                    if (buffer[i + j] != pattern[j]) { match = false; break; }
                if (match) return i;
            }
            return -1;
        }

        private byte[] TryReadRegion(MemoryRegion region)
        {
            try { return ReadBytes(region.BaseAddress, checked((int)region.Size)); }
            catch { return null; }
        }

        private IEnumerable<MemoryRegion> EnumerateReadableRegions(long startAddress, long endAddress)
        {
            long address = startAddress;
            while (address < endAddress)
            {
                MEMORY_BASIC_INFORMATION info;
                UIntPtr queried = VirtualQueryEx(_processHandle, new IntPtr(address),
                    out info, (UIntPtr)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION)));
                if (queried == UIntPtr.Zero) { address += 0x1000; continue; }

                bool committed = info.State == MemCommit;
                bool readable = IsReadableProtect(info.Protect);
                long regionBase = info.BaseAddress.ToInt64();
                long regionEnd = regionBase + info.RegionSize.ToInt64();
                long clippedBase = Math.Max(regionBase, startAddress);
                long clippedEnd = Math.Min(regionEnd, endAddress);
                long clippedSize = clippedEnd - clippedBase;

                if (committed && readable && clippedSize > 0 && clippedSize <= int.MaxValue)
                    yield return new MemoryRegion(new IntPtr(clippedBase), clippedSize);

                long next = regionEnd;
                address = next > address ? next : address + 0x1000;
            }
        }

        private static bool IsReadableProtect(uint protect)
        {
            return (protect & PageNoAccess) == 0 && (protect & PageGuard) == 0 && (protect & ReadableProtectMask) != 0;
        }

        private static byte[] BuildRelativeJumpPatch(IntPtr from, IntPtr to, int patchLength)
        {
            long relative = to.ToInt64() - from.ToInt64() - RelativeJumpLength;
            var bytes = new List<byte> { 0xE9 };
            bytes.AddRange(BitConverter.GetBytes((int)relative));
            while (bytes.Count < patchLength) bytes.Add(0x90);
            return bytes.ToArray();
        }

        private static byte[] AbsoluteJump(long target)
        {
            var bytes = new List<byte> { 0xFF, 0x25, 0x00, 0x00, 0x00, 0x00 };
            bytes.AddRange(BitConverter.GetBytes(target));
            return bytes.ToArray();
        }

        private static bool IsRel32Reachable(IntPtr from, IntPtr to)
        {
            long relative = to.ToInt64() - from.ToInt64() - RelativeJumpLength;
            return relative >= int.MinValue && relative <= int.MaxValue;
        }

        private void LogLine(string message) { Log?.Invoke(message); }

        public void Reset()
        {
            if (_processHandle != IntPtr.Zero) { CloseHandle(_processHandle); }
            _targetProcess?.Dispose();
            _targetProcess = null;
            _processHandle = IntPtr.Zero;
            _moduleBase = IntPtr.Zero;
            _moduleSize = 0;
            _energySite = IntPtr.Zero;
            _energyHookSite = IntPtr.Zero;
            _remoteEnergyCave = IntPtr.Zero;
            _energyHookOriginal = null;
            _energyHookInstalled = false;
        }

        public void Dispose() { Reset(); }

        private struct MemoryRegion
        {
            public IntPtr BaseAddress;
            public long Size;
            public MemoryRegion(IntPtr baseAddress, long size) { BaseAddress = baseAddress; Size = size; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORY_BASIC_INFORMATION
        {
            public IntPtr BaseAddress;
            public IntPtr AllocationBase;
            public uint AllocationProtect;
            public IntPtr RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MODULEINFO
        {
            public IntPtr lpBaseOfDll;
            public uint SizeOfImage;
            public IntPtr EntryPoint;
        }

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool EnumProcessModulesEx(IntPtr hProcess, IntPtr[] lphModule, int cb,
            out int lpcbNeeded, uint dwFilterFlag);

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool GetModuleInformation(IntPtr hProcess, IntPtr hModule,
            out MODULEINFO lpmodinfo, uint cb);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, out IntPtr lpNumberOfBytesRead);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int nSize, out IntPtr lpNumberOfBytesWritten);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtectEx(IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FlushInstructionCache(IntPtr hProcess, IntPtr lpBaseAddress, UIntPtr dwSize);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, UIntPtr dwSize, uint flAllocationType, uint flProtect);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern UIntPtr VirtualQueryEx(IntPtr hProcess, IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, UIntPtr dwLength);
    }
}
