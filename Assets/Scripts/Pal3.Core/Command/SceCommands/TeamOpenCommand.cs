// ---------------------------------------------------------------------------------------------
//  Copyright (c) 2021-2023, Jiaqi Liu. All rights reserved.
//  See LICENSE file in the project root for license information.
// ---------------------------------------------------------------------------------------------

namespace Pal3.Core.Command.SceCommands
{
    [SceCommand(25, "玩家队伍散开")]
    public sealed class TeamOpenCommand : ICommand
    {
        public TeamOpenCommand() { }
    }
}