using System.Runtime.InteropServices;
using System.Text;

namespace Gatto.Cli.Hardware;

//two Vulkan 1.0 calls, the device type and the memory heaps, and no logical device. the suite never runs Dump, the live check is what proves this port

//declared so CA1416 does not fire, since a zero-warnings citation counts that warning
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class VulkanReader
{
    //the ordinal of VK_STRUCTURE_TYPE_INSTANCE_CREATE_INFO
    private const uint InstanceCreateInfo = 1;

    //matches the C struct on x64, where the CLR aligns each pointer to 8
    [StructLayout(LayoutKind.Sequential)]
    private struct VkInstanceCreateInfo
    {
        public uint SType;
        public IntPtr PNext;
        public uint Flags;
        public IntPtr PApplicationInfo;
        public uint EnabledLayerCount;
        public IntPtr PpEnabledLayerNames;
        public uint EnabledExtensionCount;
        public IntPtr PpEnabledExtensionNames;
    }

    [DllImport("vulkan-1.dll")]
    private static extern int vkCreateInstance(ref VkInstanceCreateInfo info, IntPtr alloc, out IntPtr instance);

    [DllImport("vulkan-1.dll")]
    private static extern int vkEnumeratePhysicalDevices(IntPtr instance, ref uint count, IntPtr devices);

    [DllImport("vulkan-1.dll")]
    private static extern void vkGetPhysicalDeviceProperties(IntPtr device, IntPtr props);

    [DllImport("vulkan-1.dll")]
    private static extern void vkGetPhysicalDeviceMemoryProperties(IntPtr device, IntPtr props);

    [DllImport("vulkan-1.dll")]
    private static extern void vkDestroyInstance(IntPtr instance, IntPtr alloc);

    //the VkPhysicalDeviceType ordinal as the word the report prints. an unknown code keeps its number so a new kind arrives visibly
    private static string Kind(uint t) => t switch
    {
        0 => "OTHER",
        1 => "INTEGRATED_GPU",
        2 => "DISCRETE_GPU",
        3 => "VIRTUAL_GPU",
        4 => "CPU",
        _ => "UNKNOWN_" + t,
    };

    //the report's [4] section, and it never throws, every failure comes back as a failure line
    public static string Dump()
    {
        var sb = new StringBuilder();
        var instance = IntPtr.Zero;
        try
        {
            var info = new VkInstanceCreateInfo { SType = InstanceCreateInfo };
            var hr = vkCreateInstance(ref info, IntPtr.Zero, out instance);
            if (hr != 0)
            {
                sb.AppendLine("vulkan=create_instance_failed result=" + hr);
                return sb.ToString();
            }

            uint count = 0;
            vkEnumeratePhysicalDevices(instance, ref count, IntPtr.Zero);
            sb.AppendLine("vulkan=ok physical_devices=" + count);
            if (count == 0) return sb.ToString();

            var list = Marshal.AllocHGlobal((int)count * IntPtr.Size);
            var props = Marshal.AllocHGlobal(2048);   //the props buffer holds VkPhysicalDeviceProperties, about 824 bytes
            var mem = Marshal.AllocHGlobal(1024);     //the memory buffer holds VkPhysicalDeviceMemoryProperties, 520 bytes
            try
            {
                vkEnumeratePhysicalDevices(instance, ref count, list);
                for (uint i = 0; i < count; i++)
                {
                    var device = Marshal.ReadIntPtr(list, (int)i * IntPtr.Size);

                    //the properties are read by offset, five ints then deviceName, so the limits struct behind them needs no description
                    Zero(props, 2048);
                    vkGetPhysicalDeviceProperties(device, props);
                    var api = (uint)Marshal.ReadInt32(props, 0);
                    var driver = (uint)Marshal.ReadInt32(props, 4);
                    var vendor = (uint)Marshal.ReadInt32(props, 8);
                    var deviceId = (uint)Marshal.ReadInt32(props, 12);
                    var type = (uint)Marshal.ReadInt32(props, 16);
                    //read the name as UTF-8, the spec says so and PtrToStringAnsi would decode through the ANSI codepage
                    var name = Marshal.PtrToStringUTF8(props + 20);

                    sb.AppendLine($"device_{i}_name={name}");
                    sb.AppendLine($"device_{i}_type={Kind(type)} ({type})");
                    sb.AppendLine($"device_{i}_vendor=0x{vendor:X4} device_id=0x{deviceId:X4}");
                    sb.AppendLine($"device_{i}_api={(api >> 22) & 0x7F}.{(api >> 12) & 0x3FF}.{api & 0xFFF}"
                        + $" driver_raw={driver}");

                    //the memory properties by offset, and the heap array starts at 264 because VkMemoryHeap needs 8-byte alignment
                    Zero(mem, 1024);
                    vkGetPhysicalDeviceMemoryProperties(device, mem);
                    var typeCount = (uint)Marshal.ReadInt32(mem, 0);
                    var heapCount = (uint)Marshal.ReadInt32(mem, 260);
                    sb.AppendLine($"device_{i}_heap_count={heapCount} type_count={typeCount}");

                    for (uint h = 0; h < heapCount && h < 16; h++)
                    {
                        var size = (ulong)Marshal.ReadInt64(mem, 264 + (int)h * 16);
                        var flags = (uint)Marshal.ReadInt32(mem, 264 + (int)h * 16 + 8);
                        //test the flags by bit (bit 0 is device-local), an equality test would find nothing
                        var words = (flags & 1) != 0 ? "DEVICE_LOCAL" : "-";
                        if ((flags & 2) != 0) words += "|MULTI_INSTANCE";
                        sb.AppendLine($"device_{i}_heap_{h}={size} flags=0x{flags:X2} {words}");
                    }

                    for (uint t = 0; t < typeCount && t < 32; t++)
                    {
                        var propertyFlags = (uint)Marshal.ReadInt32(mem, 4 + (int)t * 8);
                        var heapIndex = (uint)Marshal.ReadInt32(mem, 4 + (int)t * 8 + 4);
                        sb.AppendLine($"device_{i}_type_{t}=heap {heapIndex} flags=0x{propertyFlags:X4}");
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(list);
                Marshal.FreeHGlobal(props);
                Marshal.FreeHGlobal(mem);
            }
        }
        catch (DllNotFoundException)
        {
            //no loader means no vendor driver on Windows, every vendor installs it with the driver
            sb.AppendLine("vulkan=absent (vulkan-1.dll not found: no vendor GPU driver)");
        }
        catch (Exception ex)
        {
            //the vulkan=error line can come after a vulkan=ok, so it overwrites the earlier verdict
            sb.AppendLine($"vulkan=error {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (instance != IntPtr.Zero)
            {
                try { vkDestroyInstance(instance, IntPtr.Zero); } catch { }
            }
        }

        return sb.ToString();
    }

    private static void Zero(IntPtr buffer, int bytes)
    {
        for (var i = 0; i < bytes; i += 8) Marshal.WriteInt64(buffer, i, 0);
    }
}
