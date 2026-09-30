namespace Gatto.Core.Hardware;

//the kind of vulkan device that supplied GraphicsMemoryBytes, read off device_i_type rather than guessed from memory arithmetic, and none means no usable device
internal enum GpuKind { None, Integrated, Discrete }
