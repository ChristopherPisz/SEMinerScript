using Sandbox.Game.EntityComponents;
using Sandbox.ModAPI.Ingame;
using SpaceEngineers.Game.ModAPI.Ingame;
using VRage.Collections;
using VRage.Game.Components;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI.Ingame.Utilities;
using VRage.Game.ModAPI.Ingame;
using VRage.Game.ObjectBuilders.Definitions;
using VRage.Game;
using VRage;
using VRageMath;
using System.Reflection.Emit;
using Sandbox.ModAPI.Interfaces;
using System.Drawing;
using System.Collections.Generic;

namespace InGameScript
{
    partial class Program : MyGridProgram
    {
        /*
        * Currently, I see no way to get a rotor to rotate by exactly 90 degrees using the command available and timer blocks or event controllers.
        * Event controllers use greater or equal to, so each quadrant would trigger multiple times after 90 degrees.
        * Therefor, I have to resort to a script I guess, where precision control is granted.
        * 
        * Scripts don't support Events, so we have to split an operation up into steps and wait for them to complete using calls out to timer blocks and back in again.
        */
        
        enum QuadBeingMined
        {
              NONE = 0   // We haven't started mining and need to get to 0 degrees
            , QUAD_1     // 0   degrees
            , QUAD_2     // 90  degrees
            , QUAD_3     // 180 degrees
            , QUAD_4     // 270 degrees
        }

        /*
            * Because we have to split operations into steps and wait for them to complete, we will save the command and its params
            */
        struct Command
        {
            public int  m_depth;      // Target depth of the drill
            public bool m_startQuad;  // Whether to mine out a quad once the target depth is reached
            public bool m_drillOn;    // Whether to turn the drill on or off   ... TODO - might be combined with the above

            public Command(int depth, bool startQuad, bool drillOn)
            {
                m_depth = depth;
                m_startQuad = startQuad;
                m_drillOn = drillOn;
            }
        };

        Command? m_outStandingCommand;          // Null if completed or no command received
        QuadBeingMined m_currentQuadBeingMined;
        float m_currentQuadColumn = 0.0f;       // Current column we are mining if mining a quadrant

        IMyMotorAdvancedStator m_rotor;
        IMyPistonBase m_sidePiston;
        IMyPistonBase m_forwardPiston;
        IMyPistonBase m_depthPiston1;
        IMyPistonBase m_depthPiston2;
        IMyPistonBase m_depthPiston3;
        IMyPistonBase m_depthPiston4;
        IMyShipDrill m_drill;

        IMyTimerBlock m_waitDepthAdjXY;         // Wait on a timer block for the pistons to be brought in for the XY plane before a depth adjustment
        IMyTimerBlock m_waitDepthAdjRotation;   // Wait on a timer block for the rotation to be set to zero degrees before a depth adjustment
        IMyTimerBlock m_waitDepthAdjZ;          // Wait on a timer block for the actual depth adjustment

        IMyTimerBlock m_waitQuadMineRotation;   // Wait on a timer block for the rotation to quadrant to be mined

        /*
        * Constructor. Called once during the session.
        * TODO - I dunno if we have to fetch these all the time. Do they fail if the block was destroyed?
        */
        Program()
        {
            Echo("Initializing AtmoMM rotor script...");

            m_currentQuadBeingMined = QuadBeingMined.NONE;

            m_waitDepthAdjXY        = GridTerminalSystem.GetBlockWithName("AtmoMM -Timer Block (Wait DepthAdj XY)") as IMyTimerBlock;
            m_waitDepthAdjRotation  = GridTerminalSystem.GetBlockWithName("AtmoMM -Timer Block (Wait DepthAdj Rotation)") as IMyTimerBlock;
            m_waitDepthAdjZ         = GridTerminalSystem.GetBlockWithName("AtmoMM -Timer Block (Wait DepthAdj Z)") as IMyTimerBlock;

            m_waitQuadMineRotation = GridTerminalSystem.GetBlockWithName("AtmoMM -Timer Block (Wait QuadMine Rotation)") as IMyTimerBlock;

            m_rotor         = GridTerminalSystem.GetBlockWithName("AtmoMM -Advanced Rotor") as IMyMotorAdvancedStator;
            m_sidePiston    = GridTerminalSystem.GetBlockWithName("AtmoMM -Piston Side") as IMyPistonBase;
            m_forwardPiston = GridTerminalSystem.GetBlockWithName("AtmoMM -Piston Forward") as IMyPistonBase;
            m_depthPiston1  = GridTerminalSystem.GetBlockWithName("AtmoMM -Piston Depth 1") as IMyPistonBase;
            m_depthPiston2  = GridTerminalSystem.GetBlockWithName("AtmoMM -Piston Depth 2") as IMyPistonBase;
            m_depthPiston3  = GridTerminalSystem.GetBlockWithName("AtmoMM -Piston Depth 3") as IMyPistonBase;
            m_depthPiston4  = GridTerminalSystem.GetBlockWithName("AtmoMM -Piston Depth 4") as IMyPistonBase;
            m_drill         = GridTerminalSystem.GetBlockWithName("AtmoMM -Drill") as IMyShipDrill;

            if (m_waitDepthAdjXY == null ||
                m_waitDepthAdjRotation == null ||
                m_waitDepthAdjZ == null ||
                m_waitQuadMineRotation == null ||
                m_rotor == null ||
                m_sidePiston == null ||
                m_forwardPiston == null ||
                m_depthPiston1 == null ||
                m_depthPiston2 == null ||
                m_depthPiston3 == null ||
                m_depthPiston4 == null ||
                m_drill == null)
            {
                Echo("Failed to get one or more blocks by name");
            }

            m_outStandingCommand = null;
            m_currentQuadColumn = 0.0f;
        }

        /*
        * Run every time a programmable block's run actions are invoked
        * Assumes we start at zero degree rotation
        */ 
        public void Main(string argument, UpdateType updateSource)
        {
            if(argument == "Start")
            {
                Echo("Start command was received");
                AdjustDepth(0, true, true);
            }
            else if (argument == "Stop")
            {
                Echo("Stop command was received");
                AdjustDepth(0, false, false);
            } 
            else if(argument == "ContinueDepthFromXY")
            {
                Echo("Continue depth adjustment from XY reset command was received");
                ContinueDepthAdjustFromXY();
            }
            else if (argument == "ContinueDepthFromRotation")
            {
                Echo("Continue depth adjustment from rotation command was received");
                ContinueDepthAdjustFromRotation();
            }
            else if( argument == "ContinueDepthFromZ")
            {
                Echo("Continue depth adjustment from Z command was received");
                ContinueDepthAdjustFromZ();
            }
            else if(argument == "ContinueQuadMineFromRotation")
            {
                Echo("Continue mining quad from rotation command was received");
                ContinueQuadMineFromRotation();
            }
            else
            {
                string message = "Error: Unrecognized command was received: {0}";
                message = string.Format(message, argument);
                Echo(message);
            }
            
        }

        public void DoNextQuad()
        {
            float angle = m_rotor.Angle;

            string message = "Current angle is {0}. Last quad mined was {1}";
            message = string.Format(message, angle, m_currentQuadBeingMined);
            Echo(message);

            if (m_currentQuadBeingMined == QuadBeingMined.NONE)
            {
                Echo("No Quad being mined. Starting Quad 1");
                m_rotor.UpperLimitDeg = 0;
                m_currentQuadBeingMined = QuadBeingMined.QUAD_1;
            }
            else if (m_currentQuadBeingMined == QuadBeingMined.QUAD_1)
            {
                Echo("Quad 1 done, rotating to Quad 2");
                m_rotor.UpperLimitDeg = 90;
                m_currentQuadBeingMined = QuadBeingMined.QUAD_2;
            }
            else if (m_currentQuadBeingMined == QuadBeingMined.QUAD_2)
            {
                Echo("Quad 2 done, rotating to Quad 3");
                m_rotor.UpperLimitDeg = 180;
                m_currentQuadBeingMined = QuadBeingMined.QUAD_3;
            }
            else if (m_currentQuadBeingMined == QuadBeingMined.QUAD_3)
            {
                Echo("Quad 3 done, rotating to Quad 4");
                m_rotor.UpperLimitDeg = 270;
                m_currentQuadBeingMined = QuadBeingMined.QUAD_4;
            }
            else if (m_currentQuadBeingMined == QuadBeingMined.QUAD_4)
            {
                Echo("Quad 4 done. Ready to increase depth or reset and finish");
                m_rotor.UpperLimitDeg = 360;
                m_currentQuadBeingMined = QuadBeingMined.NONE;
            }

            m_rotor.TargetVelocityRad = 1;

            // Wait
            m_waitQuadMineRotation.StartCountdown();
        }

        void ContinueQuadMineFromRotation()
        {
            if(m_currentQuadBeingMined == QuadBeingMined.NONE )
            {
                // Check if we need to increase depth or if we are finished

                return;
            }

            BeginMiningQuad();
        }


        /*
        * A depth adjustment consists of
        * 1) Bring in the pistons on the XY plane
        * 2) Rotate the mining assembly to zero degrees
        * 3) Adust the pistons in the Z direction for depth
        * 
        * This method is the top level method that starts the first step and waits in a timer block to proceed to the next
        */
        public void AdjustDepth(int depth, bool startQuad, bool drillOn)
        {
            m_currentQuadBeingMined = QuadBeingMined.NONE;
            m_outStandingCommand = new Command(depth, startQuad, drillOn);

            // Stop all timer blocks
            m_waitDepthAdjXY.StopCountdown();
            m_waitDepthAdjRotation.StopCountdown();
            m_waitDepthAdjZ.StopCountdown();
            m_waitQuadMineRotation.StopCountdown();

            // Z pistons to zero velocity
            m_depthPiston1.Velocity = 0;
            m_depthPiston2.Velocity = 0;
            m_depthPiston3.Velocity = 0;
            m_depthPiston4.Velocity = 0;

            // XY
            Echo("Retracting X and Y pistons for depth adjustment");
            m_sidePiston.Velocity = -0.5f;
            m_forwardPiston.Velocity = -0.5f;

            // Wait
            Echo("Calling DepthAdj XY wait timer block");
            m_waitDepthAdjXY.StartCountdown();
        }

        /*
        * A depth adjustment consists of
        * 1) Bring in the pistons on the XY plane
        * 2) Rotate the mining assembly to zero degrees
        * 3) Adust the pistons in the Z direction for depth
        * 
        * This method starts the second step and waits in a timer block to proceed to the next
        */
        private void ContinueDepthAdjustFromXY()
        {
            // Rotation
            Echo("Rotating miner assembly to zero degrees for depth adjustment");
            m_rotor.UpperLimitDeg = 0;
            m_rotor.LowerLimitDeg = 0;
            m_rotor.TargetVelocityRad = 1;

            // Wait
            Echo("Calling DepthAdj rotate wait timer block");
            m_waitDepthAdjRotation.StartCountdown();
        }

        /*
        * A depth adjustment consists of
        * 1) Bring in the pistons on the XY plane
        * 2) Rotate the mining assembly to zero degrees
        * 3) Adust the pistons in the Z direction for depth
        * 
        * This method starts the third step and waits in a timer block to proceed to the next
        */
        private void ContinueDepthAdjustFromRotation()
        {
            // Z
            if (m_outStandingCommand.HasValue)
            {
                string message = "Adjusting the miner assembly depth to target Z of {0}";
                message = string.Format(message, m_outStandingCommand.Value.m_depth);
                Echo(message);

                SetDepth(m_outStandingCommand.Value.m_depth);
            }

            // Wait
            Echo("Calling DepthAdj Z wait timer block");
            m_waitDepthAdjZ.StartCountdown();
        }

        private void ContinueDepthAdjustFromZ()
        { 
            // Turn Drill on and begin a quad or sit?
            if (m_outStandingCommand.HasValue)
            {
                m_drill.Enabled = m_outStandingCommand.Value.m_drillOn;

                if (m_outStandingCommand.Value.m_startQuad)
                {
                    Echo("Starting to mine quad");
                    DoNextQuad();
                }
                else
                {
                    Echo("Depth adjustment complete");
                }
            }

            m_outStandingCommand = null;
        }

        private float GetCurrentDepth()
        {
            return m_depthPiston1.CurrentPosition +
                   m_depthPiston2.CurrentPosition +
                   m_depthPiston3.CurrentPosition +
                   m_depthPiston4.CurrentPosition;
        }

        private void SetDepth(float targetDepth)
        {
            float numExtendedPistons = targetDepth / 10.0f;
            if( numExtendedPistons < 1.0f)
            {
                SetPistonPosition(m_depthPiston4, 0);
                SetPistonPosition(m_depthPiston3, 0);
                SetPistonPosition(m_depthPiston2, 0);
                SetPistonPosition(m_depthPiston1, targetDepth % 10.0f);
            }
            else if (numExtendedPistons < 2.0f)
            {
                SetPistonPosition(m_depthPiston4, 0);
                SetPistonPosition(m_depthPiston3, 0);
                SetPistonPosition(m_depthPiston2, targetDepth % 10.0f);
                SetPistonPosition(m_depthPiston1, 10.0f);
            }
            else if (numExtendedPistons < 3.0f)
            {
                SetPistonPosition(m_depthPiston4, 0);
                SetPistonPosition(m_depthPiston3, targetDepth % 10.0f);
                SetPistonPosition(m_depthPiston2, 10.0f);
                SetPistonPosition(m_depthPiston1, 10.0f);
            }
            else
            {
                SetPistonPosition(m_depthPiston4, targetDepth % 10.0f);
                SetPistonPosition(m_depthPiston3, 10.0f);
                SetPistonPosition(m_depthPiston2, 10.0f);
                SetPistonPosition(m_depthPiston1, 10.0f);
            }
        }

        private static void SetPistonPosition(IMyPistonBase piston, float targetPosition)
        {
            if( piston.CurrentPosition < targetPosition )
            {
                piston.MaxLimit = targetPosition;
                piston.Velocity = 0.5f;
            }
            else if (piston.CurrentPosition > targetPosition)
            {
                piston.MinLimit = targetPosition;
                piston.Velocity = -0.5f;
            }
            else
            {
                piston.Velocity = 0;
            }
        }

        /*
        * Starts mining a quadrant out of the 4 available on the current XY plane.
        * Assumes the mining assembly has the XY pistons retracted and the rotation set to the desired quadrant
        */
        private void BeginMiningQuad()
        {
            m_currentQuadColumn = 0.0f;
            MineRow();
        }

        private void MineRow()
        {
            if( m_sidePiston.CurrentPosition > 0.0f )
            {
                // Retract the piston at the rate of 0.5f
                Echo("Mining a row by retracting the side piston");
                m_sidePiston.Retract();
            }
            else
            {
                // Extend the piston at the rate of 0.5f
                Echo("Mining a row by extending the side piston");
                m_sidePiston.Extend();
            }

            // Wait
        }

        private void AdjustQuadrantColumn()
        {
            SetPistonPosition(m_forwardPiston, m_currentQuadColumn);

            // Wait
        }

    };
}

