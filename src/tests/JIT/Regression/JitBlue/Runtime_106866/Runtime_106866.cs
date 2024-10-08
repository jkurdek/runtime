// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;
using System.Runtime.CompilerServices;

// Generated by Fuzzlyn v2.3 on 2024-08-23 10:04:52
// Run on Arm64 Windows
// Seed: 12028719405363964033-vectort,vector64,vector128,armsve
// Reduced from 60.4 KiB to 0.7 KiB in 00:00:33
// Hits JIT assert in Release:
// Assertion failed '(targetReg == op1Reg) || (targetReg != op3Reg)' in 'S0:M3():this' during 'Generate code' (IL size 57; hash 0x4541fc9f; FullOpts)
//
//     File: C:\dev\dotnet\runtime2\src\coreclr\jit\hwintrinsiccodegenarm64.cpp Line: 1128
//
using System;
using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;

public struct S0
{
    public bool F0;
    public Vector<sbyte> F2;
    public void M3()
    {
        if (Sve.IsSupported)
        {
            var vr0 = this.F2;
            var vr1 = this.F2;
            var vr2 = this.F2;
            this.F2 = Sve.Splice(vr0, vr1, vr2);
            Consume(this.F0);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Consume<T>(T val)
    {
    }
}

public class Runtime_106866
{
    [Fact]
    public static void TestEntryPoint()
    {
        new S0().M3();
    }
}
