// The MelonLoader / IL2CPP net6 reference set this mod compiles against does not surface
// System.Runtime.CompilerServices.NullableAttribute. The C# compiler (LangVersion=latest) emits a
// reference to it when it generates nullable metadata for compiler-synthesized members — notably the
// async state machine and closure display-classes introduced by the multi-frame eval runner. Without
// the type present, the build fails with CS0656 "Missing compiler required member
// 'System.Runtime.CompilerServices.NullableAttribute..ctor'".
//
// Declaring the attributes here (the standard polyfill) satisfies the compiler; they carry no runtime
// behavior. Only the shapes the compiler looks up are needed.

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Property | AttributeTargets.Field |
                    AttributeTargets.Event | AttributeTargets.Parameter | AttributeTargets.ReturnValue |
                    AttributeTargets.GenericParameter, AllowMultiple = false, Inherited = false)]
    internal sealed class NullableAttribute : Attribute
    {
        public readonly byte[] NullableFlags;
        public NullableAttribute(byte flag) => NullableFlags = new[] { flag };
        public NullableAttribute(byte[] flags) => NullableFlags = flags;
    }

    [AttributeUsage(AttributeTargets.Module | AttributeTargets.Class | AttributeTargets.Struct |
                    AttributeTargets.Method | AttributeTargets.Interface | AttributeTargets.Delegate,
                    AllowMultiple = false, Inherited = false)]
    internal sealed class NullableContextAttribute : Attribute
    {
        public readonly byte Flag;
        public NullableContextAttribute(byte flag) => Flag = flag;
    }
}
