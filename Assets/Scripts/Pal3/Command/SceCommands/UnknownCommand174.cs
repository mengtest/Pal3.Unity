﻿// ---------------------------------------------------------------------------------------------
//  Copyright (c) 2021-2022, Jiaqi Liu. All rights reserved.
//  See LICENSE file in the project root for license information.
// ---------------------------------------------------------------------------------------------

namespace Pal3.Command.SceCommands
{
    #if PAL3A
    [SceCommand(174, "镜头朝向固定点左右Orbit?")]
    public class UnknownCommand174 : ICommand
    {
        public UnknownCommand174(float x, float y, float z, float duration, int mode, int synchronous)
        {
            X = x;
            Y = y;
            Z = z;
            Duration = duration;
            Mode= mode;
            Synchronous = synchronous;
        }

        public float X { get; }
        public float Y { get; }
        public float Z { get; }
        public float Duration { get; }
        public int Mode { get; }
        public int Synchronous { get; }
    }
    #endif
}