using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace WaypointCreatorGen2
{
    public partial class WaypointCreator : Form
    {
        // Dictionary<UInt32 /*CreatureID*/, Dictionary<UInt64 /*lowGUID*/, List<WaypointInfo>>>
        Dictionary<UInt32, Dictionary<UInt64, List<WaypointInfo>>> WaypointDatabyCreatureEntry = new Dictionary<UInt32, Dictionary<UInt64, List<WaypointInfo>>>();
        Dictionary<UInt32, string> CreatureNamesByEntry = new Dictionary<uint, string>();

        DataGridViewRow[] CopiedDataGridRows;
        private (uint, ulong) SelectedRow = (0, 0);
        private uint _selectedCreatureId = 0;

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

                WaypointDatabyCreatureEntry = await Task.Run(()=> GetWaypointDataFromSniff(dialog.FileName));

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

            using (System.IO.StreamReader file = new System.IO.StreamReader(filePath))
            {
                string line;
                while ((line = file.ReadLine()) != null)
                {
                    if (line.Contains("SMSG_ON_MONSTER_MOVE") || line.Contains("SMSG_ON_MONSTER_MOVE_TRANSPORT"))
                    {
                        WaypointInfo wpInfo = new WaypointInfo();
                        UInt32 creatureId = 0;
                        UInt64 lowGuid = 0;
                        string creatureName = "Unknown";

                        // Extracting the packet timestamp in milliseconds from the packet header for delay calculations
                        string[] packetHeader = line.Split(new char[] { ' ' });
                        for (int i = 0; i < packetHeader.Length; ++i)
                        {
                            if (packetHeader[i].Contains("Time:"))
                            {
                                wpInfo.TimeStamp = UInt32.Parse(TimeSpan.Parse(packetHeader[i + 2]).TotalMilliseconds.ToString());
                                break;
                            }
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
                            }

                            // Extracting spline duration
                            if (line.Contains("MoveTime:"))
                            {
                                string[] words = line.Split(new char[] { ' ' });
                                for (int i = 0; i < words.Length; ++i)
                                    if (words[i].Contains("MoveTime:"))
                                        wpInfo.MoveTime = UInt32.Parse(words[i + 1]);
                            }

                            // Extract Facing Angles
                            if (line.Contains("FaceDirection:"))
                            {
                                string[] words = line.Split(new char[] { ' ' });
                                for (int i = 0; i < words.Length; ++i)
                                    if (words[i].Contains("FaceDirection:"))
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
                                if (result.ContainsKey(creatureId) && result[creatureId].ContainsKey(lowGuid))
                                {
                                    if (result[creatureId][lowGuid].Count != 0)
                                    {
                                        int index = result[creatureId][lowGuid].Count - 1;
                                        Int64 timeDiff = wpInfo.TimeStamp - result[creatureId][lowGuid][index].TimeStamp;
                                        UInt32 oldMoveTime = result[creatureId][lowGuid][index].MoveTime;
                                        int delay = Convert.ToInt32(timeDiff - oldMoveTime);
                                        if (delay < 0)
                                            delay = 0;
                                        result[creatureId][lowGuid][index].Delay = delay;
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

                                result[creatureId][lowGuid].Add(wpInfo);
                            }

                            if (line.Contains(" WayPoints:"))
                            {
                                string[] words = line.Split(new char[] { ' ' });
                                SplinePosition splinePosition = new SplinePosition();
                                for (int i = 0; i < words.Length; ++i)
                                {
                                    if (words[i].Contains("X:"))
                                        splinePosition.PositionX = float.Parse(words[i + 1], CultureInfo.InvariantCulture);
                                    else if (words[i].Contains("Y:"))
                                        splinePosition.PositionY = float.Parse(words[i + 1], CultureInfo.InvariantCulture);
                                    else if (words[i].Contains("Z:"))
                                        splinePosition.PositionZ = float.Parse(words[i + 1], CultureInfo.InvariantCulture);
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
                        EditorListBox.Items.Add($"{waypointsByEntry.Key} - {name} ({waypointsByGuid.Key})");
                }

            }
            else
            {
                if (WaypointDatabyCreatureEntry.ContainsKey(creatureId))
                {
                    CreatureNamesByEntry.TryGetValue(creatureId, out var name);
                    foreach (var waypointsByGuid in WaypointDatabyCreatureEntry[creatureId])
                        EditorListBox.Items.Add($"{creatureId} - {name} ({waypointsByGuid.Key.ToString()})");
                }

            }
        }

        private void ShowWaypointDataForCreature(UInt32 creatureId, UInt64 lowGUID)
        {
            // Filling the GridView
            EditorGridView.Rows.Clear();
            SplineGridView.Rows.Clear();

            if (!WaypointDatabyCreatureEntry.ContainsKey(creatureId))
                return;

            _selectedCreatureId = creatureId;

            if (WaypointDatabyCreatureEntry[creatureId].ContainsKey(lowGUID))
            {
                SelectedRow = (creatureId, lowGUID);

                int count = 0;
                foreach (WaypointInfo wpInfo in WaypointDatabyCreatureEntry[creatureId][lowGUID])
                {
                    int splineCount = 0;
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
                        wpInfo.Delay);

                    foreach (SplinePosition splineInfo in wpInfo.SplineList)
                    {
                        SplineGridView.Rows.Add(
                            count,
                            splineCount,
                            splineInfo.PositionX.ToString(CultureInfo.InvariantCulture),
                            splineInfo.PositionY.ToString(CultureInfo.InvariantCulture),
                            splineInfo.PositionZ.ToString(CultureInfo.InvariantCulture));

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

            int index = aboveSelection ? EditorGridView.SelectedRows[0].Index: EditorGridView.SelectedRows[EditorGridView.SelectedRows.Count - 1].Index + 1;

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
                    rowsCopy[appendedIndex].Cells[6].Value);

            // Paste location reached, append copied rows
            foreach (DataGridViewRow row in CopiedDataGridRows)
                EditorGridView.Rows.Add(
                    index++,
                    row.Cells[1].Value,
                    row.Cells[2].Value,
                    row.Cells[3].Value,
                    row.Cells[4].Value,
                    row.Cells[5].Value,
                    row.Cells[6].Value);

            // Copied rows added, append remaining points
            for (; appendedIndex < rowsCopy.Length; ++appendedIndex)
                EditorGridView.Rows.Add(
                    appendedIndex + CopiedDataGridRows.Length,
                    rowsCopy[appendedIndex].Cells[1].Value,
                    rowsCopy[appendedIndex].Cells[2].Value,
                    rowsCopy[appendedIndex].Cells[3].Value,
                    rowsCopy[appendedIndex].Cells[4].Value,
                    rowsCopy[appendedIndex].Cells[5].Value,
                    rowsCopy[appendedIndex].Cells[6].Value);

            // GridView is updated, rebuild the graph path.
            BuildGraphPath();
        }

        private void GenerateSQLStripMenuItem_Click(object sender, EventArgs e)
        {
            // Generates the SQL output.
            // waypoint_data
            CreatureNamesByEntry.TryGetValue(_selectedCreatureId, out string name);

            SQLOutputTextBox.AppendText("SET @MOVERGUID := @CGUID+xxxxxxxx;\r\n");
            SQLOutputTextBox.AppendText($"SET @ENTRY := {_selectedCreatureId};\r\n");
            SQLOutputTextBox.AppendText("SET @PATHOFFSET := 0;\r\n");
            SQLOutputTextBox.AppendText("SET @PATH := @ENTRY * 100 + @PATHOFFSET;\r\n");
            SQLOutputTextBox.AppendText("DELETE FROM `waypoint_path` WHERE `PathId`= @PATH;\r\n");
            SQLOutputTextBox.AppendText("INSERT INTO `waypoint_path` (`PathId`, `MoveType`, `Flags`, `Comment`) VALUES\r\n");
            SQLOutputTextBox.AppendText($"(@PATH, 0, 0, '{name} - Idle');\r\n");
            SQLOutputTextBox.AppendText("\r\n");

            SQLOutputTextBox.AppendText("DELETE FROM `waypoint_path_node` WHERE `PathId`= @PATH;\r\n");
            SQLOutputTextBox.AppendText("INSERT INTO `waypoint_path_node` (`PathId`, `NodeId`, `PositionX`, `PositionY`, `PositionZ`, `Orientation`, `Delay`) VALUES\r\n");

            int rowCount = 0;
            DataGridViewRow firstRow = null;
            foreach (DataGridViewRow row in EditorGridView.Rows)
            {
                if (rowCount == 0)
                    firstRow = row;

                ++rowCount;
                if (rowCount < EditorGridView.Rows.Count)
                    SQLOutputTextBox.AppendText($"(@PATH, {row.Cells[0].Value}, {row.Cells[1].Value}, {row.Cells[2].Value}, {row.Cells[3].Value}, {row.Cells[4].Value}, {row.Cells[6].Value}),\r\n");
                else
                    SQLOutputTextBox.AppendText($"(@PATH, {row.Cells[0].Value}, {row.Cells[1].Value}, {row.Cells[2].Value}, {row.Cells[3].Value}, {row.Cells[4].Value}, {row.Cells[6].Value});\r\n");
            }

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

        private void RemoveDuplicatesButton_Click(object sender, EventArgs e)
        {
            EditorGridView.Rows.Clear();

            var wpList = WaypointDatabyCreatureEntry[SelectedRow.Item1][SelectedRow.Item2];

            var count = 0;
            foreach (var wpInfo in GetDeduplicatedWaypointList(wpList))
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
                    wpInfo.Delay);

                count++;
            }

            BuildGraphPath();
        }
    }

    public class WaypointInfo
    {
        public UInt32 TimeStamp = 0;
        public WaypointPosition Position = new WaypointPosition();
        public UInt32 MoveTime = 0;
        public Int32 Delay = 0;
        public List<SplinePosition> SplineList = new List<SplinePosition>();
    }

    public class WaypointPosition
    {
        public float PositionX = 0f;
        public float PositionY = 0f;
        public float PositionZ = 0f;
        public float? Orientation;

        public override bool Equals(object obj)
        {
            if (obj == null || GetType() != obj.GetType())
                return false;

            WaypointPosition otherPosition = (WaypointPosition)obj;
            return PositionX == otherPosition.PositionX &&
                   PositionY == otherPosition.PositionY &&
                   PositionZ == otherPosition.PositionZ;
        }

        public override int GetHashCode()
        {
            int hash = 17;
            hash = hash * 23 + PositionX.GetHashCode();
            hash = hash * 23 + PositionY.GetHashCode();
            hash = hash * 23 + PositionZ.GetHashCode();
            return hash;
        }

        public float GetEuclideanDistance(WaypointPosition pos)
        {
            float deltaX = PositionX - pos.PositionX;
            float deltaY = PositionY - pos.PositionY;
            float deltaZ = PositionZ - pos.PositionZ;

            float distanceSquared = deltaX * deltaX + deltaY * deltaY + deltaZ * deltaZ;

            return (float)Math.Sqrt(distanceSquared);
        }
    }

    public class SplinePosition
    {
        public float PositionX = 0f;
        public float PositionY = 0f;
        public float PositionZ = 0f;
    }

}
