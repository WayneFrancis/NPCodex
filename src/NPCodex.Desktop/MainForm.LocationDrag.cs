using System.Drawing;
using System.Windows.Forms;
using NPCodex.Data;

namespace NPCodex.Desktop;

public sealed partial class MainForm
{
    private void WireLocationDragAndDrop()
    {
        _campaignTree.ItemDrag += (_, e) =>
        {
            if (_busy || e.Item is not TreeNode dragged) return;
            if (dragged.Tag is not CharacterInfo and not LocationInfo) return;
            _campaignTree.DoDragDrop(dragged, DragDropEffects.Move);
        };

        _campaignTree.DragEnter += (_, e) =>
        {
            e.Effect = CanDropTreeNode(e) ? DragDropEffects.Move : DragDropEffects.None;
        };
        _campaignTree.DragOver += (_, e) =>
        {
            e.Effect = CanDropTreeNode(e) ? DragDropEffects.Move : DragDropEffects.None;
        };

        _campaignTree.DragDrop += async (_, e) =>
        {
            if (!CanDropTreeNode(e) ||
                e.Data?.GetData(typeof(TreeNode)) is not TreeNode source)
                return;
            var target = _campaignTree.GetNodeAt(
                _campaignTree.PointToClient(new Point(e.X, e.Y)));
            var parentLocationId = target?.Tag is LocationInfo location
                ? location.Id : (long?)null;

            await RunBusyAsync(async () =>
            {
                if (CampaignId is not long campaignId)
                    throw new InvalidOperationException("Select a campaign.");
                long movedId;
                bool movedLocation;
                if (source.Tag is CharacterInfo character)
                {
                    movedLocation = false;
                    movedId = character.Id;
                    await _database.MoveCharacterAsync(
                        campaignId, character.Id, parentLocationId);
                }
                else if (source.Tag is LocationInfo current)
                {
                    movedLocation = true;
                    movedId = current.Id;
                    await _database.MoveLocationAsync(
                        campaignId, current.Id, parentLocationId);
                }
                else return;

                // Clear a filter that could otherwise hide the moved item.
                _treeFilter.Clear();
                await LoadCharactersAsync();
                var node = FindTreeNode(n =>
                    movedLocation ? n.Tag is LocationInfo l && l.Id == movedId :
                                    n.Tag is CharacterInfo c && c.Id == movedId);
                if (node is not null)
                {
                    var oldFlag = _updatingTree;
                    _updatingTree = true;
                    try
                    {
                        _campaignTree.SelectedNode = node;
                        node.EnsureVisible();
                    }
                    finally { _updatingTree = oldFlag; }
                }
                _status.Text = movedLocation
                    ? "Location and its child locations moved."
                    : parentLocationId is null
                        ? "Character moved to Unassigned."
                        : "Character moved to the selected location.";
            });
        };
    }

    private bool CanDropTreeNode(DragEventArgs args)
    {
        if (_busy || args.Data?.GetData(typeof(TreeNode)) is not TreeNode source)
            return false;
        if (source.Tag is not CharacterInfo && source.Tag is not LocationInfo)
            return false;
        var target = _campaignTree.GetNodeAt(
            _campaignTree.PointToClient(new Point(args.X, args.Y)));
        // Drop on a location, on Unassigned, or into empty tree space.
        // Unassigned and blank space mean "no parent".
        if (target is not null &&
            target.Tag is not LocationInfo &&
            !ReferenceEquals(target.Tag, _unassignedNodeTag))
            return false;

        if (source.Tag is not LocationInfo moving) return true;
        if (target?.Tag is not LocationInfo parent) return true;
        if (parent.Id == moving.Id) return false;

        // Reject attempting to move a location beneath any of its descendants.
        var currentId = parent.Id;
        var visited = new HashSet<long>();
        while (visited.Add(currentId))
        {
            if (currentId == moving.Id) return false;
            var current = _campaignLocations.FirstOrDefault(l => l.Id == currentId);
            if (current?.ParentLocationId is not long ancestor) break;
            currentId = ancestor;
        }
        return true;
    }
}
