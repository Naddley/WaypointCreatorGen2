using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Numerics;
using WaypointCreatorGen2.Enums;
using WaypointCreatorGen2.Spline;

namespace WaypointCreatorGen2
{
    public partial class WaypointCreator : Form
    {
        public System.Drawing.Color VirtualPointColor = System.Drawing.Color.LightBlue;

        // Dictionary<UInt32 /*CreatureID*/, Dictionary<UInt64 /*lowGUID*/, List<WaypointInfo>>>
        Dictionary<UInt32, Dictionary<UInt64, List<WaypointInfo>>> WaypointDatabyCreatureEntry = new Dictionary<UInt32, Dictionary<UInt64, List<WaypointInfo>>>();
        Dictionary<UInt32, string> CreatureNamesByEntry = new Dictionary<uint, string>();

        DataGridViewRow[] CopiedDataGridRows;
        private SelectedRowData SelectedRow = new()
        {
            CreatureID = 0,
            LowGUID = 0,
            FirstInfo = null,
        };
        private const int SEQUENCE_TO_CHECK_FOR_DUPLICATES = 3;

        public WaypointCreator()
        {
            InitializeComponent();
            GridViewContextMenuStrip.Enabled = false;
        }

        private void WaypointCreator_Load(object sender, EventArgs e)
        {

        }

        private async void EditorImportSniffButton_Click(object sender, EventArgs e)
        {
            OpenFileDialog dialog = new OpenFileDialog();
            dialog.Filter = "txt files (*.txt)|*.txt|All files (*.*)|*.*";
            dialog.FilterIndex = 1;
            dialog.RestoreDirectory = true;

            if (dialog.ShowDialog() == DialogResult.OK)
            {
                EditorListBox.Items.Clear();
                EditorGridView.Rows.Clear();
                EditorWaypointChart.Series["Path"].Points.Clear();
                EditorWaypointChart.Series["Line"].Points.Clear();
                EditorImportSniffButton.Enabled = false;
                EditorFilterEntryButton.Enabled = false;
                GridViewContextMenuStrip.Enabled = false;
                EditorLoadingLabel.Text = "Loading [" + Path.GetFileName(dialog.FileName) + "]...";

                WaypointDatabyCreatureEntry = await Task.Run(() => GetWaypointDataFromSniff(dialog.FileName));

                EditorImportSniffButton.Enabled = true;
                EditorFilterEntryButton.Enabled = true;
                EditorLoadingLabel.Text = "Loaded [" + Path.GetFileName(dialog.FileName) + "].";
                ListEntries(0); // Initially listing all available GUIDs
            }
        }

        // Parses all waypoint data from the provided file and returns a container filled with all needed data
        private Dictionary<UInt32, Dictionary<UInt64, List<WaypointInfo>>> GetWaypointDataFromSniff(String filePath)
        {
            CreatureNamesByEntry.Clear();
            Dictionary<UInt32, Dictionary<UInt64, List<WaypointInfo>>> result = new Dictionary<UInt32, Dictionary<UInt64, List<WaypointInfo>>>();

            var SplineFlagsRegEx = new Regex(@"^\[[0-9]+\]\sSplineFlags: ([0-9]+)");

            using (System.IO.StreamReader file = new System.IO.StreamReader(filePath))
            {
                string line;
                while ((line = file.ReadLine()) != null)
                {
                    if (line.Contains("SMSG_ON_MONSTER_MOVE") || line.Contains("SMSG_ON_MONSTER_MOVE_TRANSPORT") || line.Contains("SMSG_UPDATE_OBJECT"))
                    {
                        WaypointInfo wpInfo = new WaypointInfo();
                        UInt32 creatureId = 0;
                        UInt64 lowGuid = 0;
                        string creatureName = "Unknown";
                        bool isUpdateObject = line.Contains("SMSG_UPDATE_OBJECT");
                        uint moverCount = 0;

                        if (isUpdateObject)
                            wpInfo.Comment += "UpdateObject ";

                        // Extracting the packet timestamp in milliseconds from the packet header for delay calculations
                        string[] packetHeader = line.Split(new char[] { ' ' });
                        for (int i = 0; i < packetHeader.Length; ++i)
                        {
                            if (packetHeader[i].Contains("Time:"))
                                wpInfo.TimeStamp = DateTime.ParseExact($"{packetHeader[i + 1]} {packetHeader[i + 2]}", "MM/dd/yyyy HH:mm:ss.fff", CultureInfo.InvariantCulture);

                            if (packetHeader[i].Contains("Number:"))
                                wpInfo.PacketNum = ulong.Parse(packetHeader[i + 1]);
                        }

                        // Header noted, reading rest of the packet now
                        do
                        {
                            // Skip chase movement
                            if (line.Contains("Face:") && line.Contains("FacingTarget"))
                                break;

                            // Extracting entry and lowGuid from packet
                            if (line.Contains("MoverGUID:"))
                            {
                                string[] words = line.Split(new char[] { ' ' });
                                for (int i = 0; i < words.Length; ++i)
                                {
                                    if (words[i].Contains("Entry:"))
                                        creatureId = UInt32.Parse(words[i + 1]);
                                    else if (words[i].Contains("Low:"))
                                        lowGuid = UInt64.Parse(words[i + 1]);
                                }

                                // Skip invalid data.
                                if (creatureId == 0 || lowGuid == 0)
                                    break;

                                string pattern = @"Entry:\s\d+\s\((.*)\)";
                                Regex regex = new Regex(pattern);
                                Match match = regex.Match(line);

                                if (match.Success)
                                    creatureName = match.Groups[1].Value;

                                moverCount++;
                            }

                            if (line.Contains("SplineFlags:"))
                            {
                                var match = SplineFlagsRegEx.Match(line);
                                if (match.Success)
                                    wpInfo.SplineFlags = (MoveSplineFlag)Enum.Parse(typeof(MoveSplineFlag), match.Groups[1].Value);
                            }

                            // Extracting spline duration
                            if (line.Contains("MoveTime:"))
                            {
                                string[] words = line.Split(new char[] { ' ' });
                                for (int i = 0; i < words.Length; ++i)
                                    if (words[i].Contains("MoveTime:") && !words[i].Contains("Has"))
                                        wpInfo.MoveTime = UInt32.Parse(words[i + 1]);
                            }
                            else if (line.Contains("Duration:"))
                            {
                                string[] words = line.Split(new char[] { ' ' });
                                for (int i = 0; i < words.Length; ++i)
                                    if (words[i].Contains("Duration:"))
                                        wpInfo.MoveTime = UInt32.Parse(words[i + 1]);
                            }

                            // Extract Facing Angles
                            if (line.Contains("FaceDirection:"))
                            {
                                string[] words = line.Split(new char[] { ' ' });
                                for (int i = 0; i < words.Length; ++i)
                                    if (words[i].StartsWith("FaceDirection:"))
                                        wpInfo.Position.Orientation = float.Parse(words[i + 1], CultureInfo.InvariantCulture);
                            }

                            // Extracting waypoint (The space in the string is intentional. Do not remove!)
                            if (line.Contains(" Points:"))
                            {
                                string[] words = line.Split(new char[] { ' ' });
                                for (int i = 0; i < words.Length; ++i)
                                {
                                    if (words[i].Contains("X:"))
                                        wpInfo.Position.PositionX = float.Parse(words[i + 1], CultureInfo.InvariantCulture);
                                    else if (words[i].Contains("Y:"))
                                        wpInfo.Position.PositionY = float.Parse(words[i + 1], CultureInfo.InvariantCulture);
                                    else if (words[i].Contains("Z:"))
                                        wpInfo.Position.PositionZ = float.Parse(words[i + 1], CultureInfo.InvariantCulture);
                                }

                                // Delay Calculation
                                if (!isUpdateObject)
                                {
                                    if (result.ContainsKey(creatureId) && result[creatureId].ContainsKey(lowGuid))
                                    {
                                        if (result[creatureId][lowGuid].Count != 0)
                                        {
                                            int index = result[creatureId][lowGuid].Count - 1;
                                            Int64 timeDiff = (Int64)(wpInfo.TimeStamp - result[creatureId][lowGuid][index].TimeStamp).TotalMilliseconds;
                                            UInt32 oldMoveTime = result[creatureId][lowGuid][index].MoveTime;
                                            int delay = 0;
                                            if (oldMoveTime < 3 * 60 * 1000) // 3min, arbitrary number as MoveTime in UpdateObject might have high values causing overflows
                                                delay = Convert.ToInt32(timeDiff - oldMoveTime);

                                            if (delay < 0)
                                                delay = 0;
                                            result[creatureId][lowGuid][index].Delay = delay;
                                        }
                                    }
                                }

                                // Everything gathered, time to store the data
                                if (!result.ContainsKey(creatureId))
                                {
                                    CreatureNamesByEntry.Add(creatureId, creatureName);
                                    result.Add(creatureId, new Dictionary<UInt64, List<WaypointInfo>>());
                                }

                                if (!result[creatureId].ContainsKey(lowGuid))
                                    result[creatureId].Add(lowGuid, new List<WaypointInfo>());

                                result[creatureId][lowGuid].Add(new WaypointInfo(wpInfo));
                            }

                            if (line.Contains(" WayPoints:"))
                            {
                                string[] words = line.Split(new char[] { ' ' });
                                Vector3 splinePosition = new Vector3();
                                for (int i = 0; i < words.Length; ++i)
                                {
                                    if (words[i].Contains("X:"))
                                        splinePosition.X = float.Parse(words[i + 1], CultureInfo.InvariantCulture);
                                    else if (words[i].Contains("Y:"))
                                        splinePosition.Y = float.Parse(words[i + 1], CultureInfo.InvariantCulture);
                                    else if (words[i].Contains("Z:"))
                                        splinePosition.Z = float.Parse(words[i + 1], CultureInfo.InvariantCulture);
                                }

                                wpInfo.SplineList.Add(splinePosition);
                            }
                        }
                        while ((line = file.ReadLine()) != "");
                    }
                }
            }
            return result;
        }

        private void ListEntries(UInt32 creatureId)
        {
            EditorListBox.Items.Clear();

            if (creatureId == 0)
            {
                foreach (var waypointsByEntry in WaypointDatabyCreatureEntry)
                {
                    CreatureNamesByEntry.TryGetValue(waypointsByEntry.Key, out var name);
                    foreach (var waypointsByGuid in waypointsByEntry.Value)
                    {
                        string cyclic = "";
                        if (waypointsByGuid.Value[0].IsCyclic()) // Cyclic
                            cyclic += "Cyclic ";
                        if (waypointsByGuid.Value[0].IsEnterCycle()) // EnterCycle
                            cyclic += "Enter Cycle ";
                        EditorListBox.Items.Add($"{waypointsByEntry.Key} - {name} ({waypointsByGuid.Key}){cyclic}");
                    }
                }

            }
            else
            {
                if (WaypointDatabyCreatureEntry.ContainsKey(creatureId))
                {
                    CreatureNamesByEntry.TryGetValue(creatureId, out var name);
                    foreach (var waypointsByGuid in WaypointDatabyCreatureEntry[creatureId])
                    {
                        string cyclic = "";
                        if (waypointsByGuid.Value[0].IsCyclic()) // Cyclic
                            cyclic += "Cyclic ";
                        if (waypointsByGuid.Value[0].IsEnterCycle()) // EnterCycle
                            cyclic += "Enter Cycle ";
                        EditorListBox.Items.Add($"{creatureId} - {name} ({waypointsByGuid.Key}){cyclic}");
                    }
                }
            }
        }

        public bool IsCyclicVirtualPoint(WaypointInfo wpInfo, int count, int maxCount)
        {
            if (!wpInfo.IsCyclic())
                return false;

            if (count < 2)
                return true;

            int skipLastNum = wpInfo.IsEnterCycle() ? 2 : 1;
            return (maxCount - count) <= skipLastNum;
        }

        public void AddNodeToDataGrid(WaypointInfo wpInfo, int count, int maxCount, int pointIndex)
        {
            string orientation = "NULL";
            if (wpInfo.Position.Orientation.HasValue)
                orientation = wpInfo.Position.Orientation.Value.ToString(CultureInfo.InvariantCulture);

            EditorGridView.Rows.Add(
                count,
                wpInfo.Position.PositionX.ToString(CultureInfo.InvariantCulture),
                wpInfo.Position.PositionY.ToString(CultureInfo.InvariantCulture),
                wpInfo.Position.PositionZ.ToString(CultureInfo.InvariantCulture),
                orientation,
                wpInfo.MoveTime,
                wpInfo.Delay,
                pointIndex);

            if (IsCyclicVirtualPoint(wpInfo, count, maxCount))
                EditorGridView.Rows[EditorGridView.Rows.Count - 1].DefaultCellStyle.BackColor = VirtualPointColor;
        }

        private void ShowWaypointDataForCreature(UInt32 creatureId, UInt64 lowGUID)
        {
            // Filling the GridView
            EditorGridView.Rows.Clear();
            SplineGridView.Rows.Clear();

            if (!WaypointDatabyCreatureEntry.ContainsKey(creatureId))
                return;

            SelectedRow.CreatureID = creatureId;

            if (WaypointDatabyCreatureEntry[creatureId].ContainsKey(lowGUID))
            {
                SelectedRow.CreatureID = creatureId;
                SelectedRow.LowGUID = lowGUID;
                SelectedRow.FirstInfo = WaypointDatabyCreatureEntry[creatureId][lowGUID].FirstOrDefault();

                int count = 0;
                int maxCount = WaypointDatabyCreatureEntry[creatureId][lowGUID].Count;
                foreach (WaypointInfo wpInfo in WaypointDatabyCreatureEntry[creatureId][lowGUID])
                {
                    AddNodeToDataGrid(wpInfo, count, maxCount, count);

                    int splineCount = 0;
                    foreach (Vector3 splineInfo in wpInfo.SplineList)
                    {
                        SplineGridView.Rows.Add(
                            count,
                            splineCount,
                            splineInfo.X.ToString(CultureInfo.InvariantCulture),
                            splineInfo.Y.ToString(CultureInfo.InvariantCulture),
                            splineInfo.Z.ToString(CultureInfo.InvariantCulture));

                        ++splineCount;
                    }

                    ++count;
                }
            }

            BuildGraphPath();
            GridViewContextMenuStrip.Enabled = true;
        }
        private void BuildGraphPath()
        {
            EditorWaypointChart.ChartAreas[0].AxisX.ScaleView.ZoomReset();
            EditorWaypointChart.ChartAreas[0].AxisY.ScaleView.ZoomReset();

            EditorWaypointChart.Series["Path"].Points.Clear();
            EditorWaypointChart.Series["Line"].Points.Clear();

            foreach (DataGridViewRow dataRow in EditorGridView.Rows)
            {
                float x = float.Parse(dataRow.Cells[1].Value.ToString(), CultureInfo.InvariantCulture);
                float y = float.Parse(dataRow.Cells[2].Value.ToString(), CultureInfo.InvariantCulture);

                EditorWaypointChart.Series["Path"].Points.AddXY(x, y);
                EditorWaypointChart.Series["Path"].Points[dataRow.Index].Label = dataRow.Index.ToString();
                EditorWaypointChart.Series["Line"].Points.AddXY(x, y);
            }
        }

        // Filters the ListBox entries by CreatureID
        private void EditorFilterEntryButton_Click(object sender, EventArgs e)
        {
            UInt32 creatureId = 0;
            UInt32.TryParse(EditorFilterEntryTextBox.Text, out creatureId);
            ListEntries(creatureId);
        }

        private void EditorListBox_SelectedValueChanged(object sender, EventArgs e)
        {
            if (EditorListBox.SelectedIndex == -1)
                return;

            string pattern = @"^(\d+).*\((\d+)\)";

            Regex regex = new Regex(pattern);
            Match match = regex.Match(EditorListBox.SelectedItem.ToString());
            if (match.Success)
            {
                uint creatureId = uint.Parse(match.Groups[1].Value);
                ulong lowGuid = ulong.Parse(match.Groups[2].Value);
                ShowWaypointDataForCreature(creatureId, lowGuid);
            }
        }

        private void CutStripMenuItem_Click(object sender, EventArgs e)
        {
            if (EditorGridView.SelectedRows.Count == 0)
                return;

            CopiedDataGridRows = new DataGridViewRow[EditorGridView.SelectedRows.Count];
            EditorGridView.SelectedRows.CopyTo(CopiedDataGridRows, 0);

            foreach (DataGridViewRow row in EditorGridView.SelectedRows)
                EditorGridView.Rows.Remove(row);

            // Update the row count field
            int count = 0;
            foreach (DataGridViewRow row in EditorGridView.Rows)
            {
                row.Cells[0].Value = count;
                ++count;
            }

            // GriwView is updated, rebuild the graph path.
            BuildGraphPath();
        }

        private void CopyStripMenuItem_Click(object sender, EventArgs e)
        {
            if (EditorGridView.SelectedRows.Count == 0)
                return;

            CopiedDataGridRows = new DataGridViewRow[EditorGridView.SelectedRows.Count];
            EditorGridView.SelectedRows.CopyTo(CopiedDataGridRows, 0);
        }

        private void PasteAboveStripMenuItem_Click(object sender, EventArgs e)
        {
            PasteRows(true);
        }

        private void PasteBelowStripMenuItem_Click(object sender, EventArgs e)
        {
            PasteRows(false);
        }

        private void PasteRows(bool aboveSelection)
        {
            if (CopiedDataGridRows == null || CopiedDataGridRows.Length == 0 || EditorGridView.SelectedRows.Count == 0)
                return;

            int index = aboveSelection ? EditorGridView.SelectedRows[0].Index : EditorGridView.SelectedRows[EditorGridView.SelectedRows.Count - 1].Index + 1;

            DataGridViewRow[] rowsCopy = new DataGridViewRow[EditorGridView.Rows.Count];
            EditorGridView.Rows.CopyTo(rowsCopy, 0);
            EditorGridView.Rows.Clear();
            Array.Reverse(CopiedDataGridRows);

            int appendedIndex = 0;
            // First we append all waypoints that we had before the paste location
            for (; appendedIndex < index; ++appendedIndex)
                EditorGridView.Rows.Add(
                    appendedIndex,
                    rowsCopy[appendedIndex].Cells[1].Value,
                    rowsCopy[appendedIndex].Cells[2].Value,
                    rowsCopy[appendedIndex].Cells[3].Value,
                    rowsCopy[appendedIndex].Cells[4].Value,
                    rowsCopy[appendedIndex].Cells[5].Value,
                    rowsCopy[appendedIndex].Cells[6].Value,
                    rowsCopy[appendedIndex].Cells[7].Value);

            // Paste location reached, append copied rows
            foreach (DataGridViewRow row in CopiedDataGridRows)
                EditorGridView.Rows.Add(
                    index++,
                    row.Cells[1].Value,
                    row.Cells[2].Value,
                    row.Cells[3].Value,
                    row.Cells[4].Value,
                    row.Cells[5].Value,
                    row.Cells[6].Value,
                    row.Cells[7].Value);

            // Copied rows added, append remaining points
            for (; appendedIndex < rowsCopy.Length; ++appendedIndex)
                EditorGridView.Rows.Add(
                    appendedIndex + CopiedDataGridRows.Length,
                    rowsCopy[appendedIndex].Cells[1].Value,
                    rowsCopy[appendedIndex].Cells[2].Value,
                    rowsCopy[appendedIndex].Cells[3].Value,
                    rowsCopy[appendedIndex].Cells[4].Value,
                    rowsCopy[appendedIndex].Cells[5].Value,
                    rowsCopy[appendedIndex].Cells[6].Value,
                    rowsCopy[appendedIndex].Cells[7].Value);

            // GridView is updated, rebuild the graph path.
            BuildGraphPath();
        }

        private void GenerateSQLStripMenuItem_Click(object sender, EventArgs e)
        {
            // Generates the SQL output.
            // waypoint_data
            CreatureNamesByEntry.TryGetValue(SelectedRow.CreatureID, out string name);

            var velocity = "NULL";
            var moveType = WaypointMoveType.Walk;
            var flags = 0;
            if (SelectedRow.FirstInfo != null)
            {
                if (SelectedRow.FirstInfo.IsCyclic() || SelectedRow.FirstInfo.IsUncompressedPath())
                    flags = 0x2;

                if (SelectedRow.FirstInfo.IsCatmullrom())
                {
                    List<Vector3> points = new List<Vector3>();
                    foreach (DataGridViewRow row in EditorGridView.Rows)
                    {
                        points.Add(new Vector3(float.Parse((string)row.Cells[1].Value, CultureInfo.InvariantCulture), float.Parse((string)row.Cells[2].Value, CultureInfo.InvariantCulture), float.Parse((string)row.Cells[3].Value, CultureInfo.InvariantCulture)));
                    }
                    velocity = CatmullRom.CalculateSpeed(SelectedRow.FirstInfo.MoveTime, points).ToString("F4", CultureInfo.InvariantCulture);
                }
            }

            SQLOutputTextBox.AppendText("SET @MOVERGUID := @CGUID+xxxxxxxx;\r\n");
            SQLOutputTextBox.AppendText($"SET @ENTRY := {SelectedRow.CreatureID};\r\n");
            SQLOutputTextBox.AppendText("SET @PATHOFFSET := 0;\r\n");
            SQLOutputTextBox.AppendText("SET @PATH := @ENTRY * 100 + @PATHOFFSET;\r\n");
            SQLOutputTextBox.AppendText("DELETE FROM `waypoint_path` WHERE `PathId`= @PATH;\r\n");
            SQLOutputTextBox.AppendText("INSERT INTO `waypoint_path` (`PathId`, `MoveType`, `Flags`, `Velocity`, `Comment`) VALUES\r\n");
            SQLOutputTextBox.AppendText($"(@PATH, {(int)moveType}, 0x{flags:x}, {velocity}, '{name} - Idle');\r\n");
            SQLOutputTextBox.AppendText("\r\n");

            SQLOutputTextBox.AppendText("DELETE FROM `waypoint_path_node` WHERE `PathId`= @PATH;\r\n");
            SQLOutputTextBox.AppendText("INSERT INTO `waypoint_path_node` (`PathId`, `NodeId`, `PositionX`, `PositionY`, `PositionZ`, `Orientation`, `Delay`) VALUES\r\n");

            int rowCount = 0;
            DataGridViewRow firstRow = null;
            foreach (DataGridViewRow row in EditorGridView.Rows)
            {
                if (row.DefaultCellStyle.BackColor == VirtualPointColor)
                    continue;

                if (rowCount == 0)
                    firstRow = row;

                ++rowCount;
                var nodeId = rowCount - 1;
                SQLOutputTextBox.AppendText($"(@PATH, {nodeId}, {row.Cells[1].Value}, {row.Cells[2].Value}, {row.Cells[3].Value}, {row.Cells[4].Value}, {row.Cells[6].Value}),\r\n");
            }

            // replace last comma with semicolon to end the statement
            string output = SQLOutputTextBox.Text;
            int lastCommaIdx = output.LastIndexOf(',');
            output = output.Remove(lastCommaIdx, 1).Insert(lastCommaIdx, ";");
            SQLOutputTextBox.Text = output;

            SQLOutputTextBox.AppendText("\r\n");

            // creature
            if (firstRow != null)
                SQLOutputTextBox.AppendText($"UPDATE `creature` SET `position_x`={firstRow.Cells[1].Value}, `position_y`={firstRow.Cells[2].Value}, `position_z`={firstRow.Cells[3].Value}, `orientation`=0, `wander_distance`=0, `MovementType`=2 WHERE `guid`=@MOVERGUID;\r\n");

            // creature_addon
            SQLOutputTextBox.AppendText("DELETE FROM `creature_addon` WHERE `guid`=@MOVERGUID;\r\n");
            SQLOutputTextBox.AppendText("INSERT INTO `creature_addon` (`guid`, `PathId`, `SheathState`) VALUES\r\n");
            SQLOutputTextBox.AppendText("(@MOVERGUID, @PATH, 1);\r\n");
            SQLOutputTextBox.AppendText("\r\n");
            SQLOutputTextBox.AppendText("\r\n");

            TabControl.SelectedTab = TabControl.TabPages[1];
        }

        private void SQLOutputSaveButton_Click(object sender, EventArgs e)
        {
            // Saving the text of the SQLOutputTextBox into a file
            SaveFileDialog dialog = new SaveFileDialog();
            dialog.Filter = "Structured Query Language (*.sql)|*.sql|All files (*.*)|*.*";
            dialog.FilterIndex = 1;
            dialog.DefaultExt = "sql";
            dialog.RestoreDirectory = true;

            if (dialog.ShowDialog() == DialogResult.OK)
                File.WriteAllText(dialog.FileName, SQLOutputTextBox.Text, System.Text.Encoding.UTF8);
        }

        int GetIndexOfWaypointInfo(List<WaypointInfo> wpList, WaypointInfo wpInfo, int startIndex)
        {
            for (var i = startIndex; i < wpList.Count; i++)
            {
                if (wpList[i].Position.Equals(wpInfo.Position))
                    return i;
            }
            return -1;
        }

        List<WaypointInfo> GetDeduplicatedWaypointList(List<WaypointInfo> wpList)
        {
            List<WaypointInfo> uniqueWpInfo = new List<WaypointInfo>();
            var sequenceIndex = -1;
            var duplicateSequenceFound = 0;
            var duplicateSequenceStart = 0;
            for (var i = 0; i < wpList.Count; i++)
            {
                WaypointInfo wpInfo = wpList[i];

                // skip unnecessary duplicate chains of same coords
                if (uniqueWpInfo.Count > 0 && wpInfo.Position.GetEuclideanDistance(uniqueWpInfo.Last().Position) < 0.5f)
                    continue;

                var newIndex = i + 1;
                if (sequenceIndex != -1)
                {
                    newIndex = sequenceIndex;
                    sequenceIndex = GetIndexOfWaypointInfo(wpList, wpInfo, newIndex);
                    if (sequenceIndex != -1 && newIndex + 1 == sequenceIndex)
                    {
                        duplicateSequenceFound++;
                        if (duplicateSequenceFound >= SEQUENCE_TO_CHECK_FOR_DUPLICATES)
                        {
                            // add remaining elements until duplicate sequence
                            for (var k = i; k < duplicateSequenceStart; k++)
                            {
                                if (wpList[k].Position.GetEuclideanDistance(uniqueWpInfo.Last().Position) < 1.0f)
                                    continue;

                                // skip if start is duplicated at the end because it was found inbetween sequence
                                if (k == duplicateSequenceStart - 1 && wpList[k].Position.Equals(uniqueWpInfo.First().Position))
                                    continue;
                                uniqueWpInfo.Add(wpList[k]);
                            }
                            break;
                        }
                    }
                    else
                    {
                        duplicateSequenceFound = 0;
                        duplicateSequenceStart = sequenceIndex;
                    }
                }
                else
                {
                    sequenceIndex = GetIndexOfWaypointInfo(wpList, wpInfo, newIndex);
                    duplicateSequenceFound = sequenceIndex != -1 ? 1 : 0;
                    duplicateSequenceStart = sequenceIndex;
                }
                uniqueWpInfo.Add(wpInfo);
            }
            return uniqueWpInfo;
        }

        private void RemovePointsFromOtherPackets()
        {
            EditorGridView.Rows.Clear();

            var wpList = WaypointDatabyCreatureEntry[SelectedRow.CreatureID][SelectedRow.LowGUID];
            var maxCount = wpList.Count;
            ulong? currentPacketNum = null;
            for (var count = 0; count < maxCount; count++)
            {
                var wpInfo = wpList[count];

                if (wpInfo.IsCyclic() && currentPacketNum != null && wpInfo.PacketNum != currentPacketNum)
                    break;

                currentPacketNum = wpInfo.PacketNum;
                AddNodeToDataGrid(wpInfo, count, maxCount, wpList.IndexOf(wpInfo));
            }

            EditorGridView.Rows[EditorGridView.Rows.Count - 1].DefaultCellStyle.BackColor = VirtualPointColor;

            BuildGraphPath();
        }

        private void RemoveDuplicatePoints()
        {
            EditorGridView.Rows.Clear();

            var wpList = WaypointDatabyCreatureEntry[SelectedRow.CreatureID][SelectedRow.LowGUID];

            var dedupList = GetDeduplicatedWaypointList(wpList);
            var maxCount = dedupList.Count;
            for (var count = 0; count < maxCount; count++)
            {
                var wpInfo = wpList[count];
                AddNodeToDataGrid(wpInfo, count, maxCount, wpList.IndexOf(wpInfo));
            }

            BuildGraphPath();
        }

        private void RemoveDuplicatesButton_Click(object sender, EventArgs e)
        {
            if (SelectedRow.FirstInfo == null)
                return;

            if (SelectedRow.FirstInfo.IsCyclic())
                RemovePointsFromOtherPackets();
            else
                RemoveDuplicatePoints();
        }

        private void EditorGridView_RowsRemoved(object sender, DataGridViewRowsRemovedEventArgs e)
        {
            if (!SelectedRow.FirstInfo.IsCyclic())
                return;

            var count = 0;
            var maxCount = EditorGridView.Rows.Count;
            for (count = 0; count < maxCount; count++)
            {
                if (IsCyclicVirtualPoint(SelectedRow.FirstInfo, count, EditorGridView.Rows.Count))
                    EditorGridView.Rows[count].DefaultCellStyle.BackColor = VirtualPointColor;
                else
                    EditorGridView.Rows[count].DefaultCellStyle.BackColor = System.Drawing.Color.White;
            }
        }
    }
}
