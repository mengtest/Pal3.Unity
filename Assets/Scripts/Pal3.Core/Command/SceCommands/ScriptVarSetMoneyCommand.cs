﻿// ---------------------------------------------------------------------------------------------
//  Copyright (c) 2021-2023, Jiaqi Liu. All rights reserved.
//  See LICENSE file in the project root for license information.
// ---------------------------------------------------------------------------------------------

namespace Pal3.Core.Command.SceCommands
{
    [SceCommand(49, "取出当前金钱数并赋值给变量，" +
                    "参数：变量名", 0b0001)]
    public sealed class ScriptVarSetMoneyCommand : ICommand
    {
        public ScriptVarSetMoneyCommand(ushort variable)
        {
            Variable = variable;
        }

        [SceUserVariable] public ushort Variable { get; }
    }
}