using System.Numerics;
using Phalanx.World;
using Xunit;

namespace Phalanx.Tests;

// Entity transform history (Entity.cpp:1014-1045): one capture per committed epoch, before the first change
[Collection("World")]
public class TransformHistoryTests
{
    [Fact]
    public void The_previous_matrix_is_the_pose_before_the_first_change_of_the_frame()
    {
        World.World.SetMainThread(); // the hierarchy is main thread only
        var entity = new Entity();
        entity.SetPosition(new Vector3(1, 0, 0));
        // no history before its first transform: previous is the current pose, not the identity
        Assert.Equal(entity.GetMatrix(), entity.GetMatrixPrevious());

        Entity.CommitTransformHistory();
        Assert.Equal(entity.GetMatrix(), entity.GetMatrixPrevious()); // unchanged this frame

        Matrix4x4 rendered = entity.GetMatrix();
        entity.SetPosition(new Vector3(2, 0, 0));
        entity.SetPosition(new Vector3(3, 0, 0)); // repeated edits keep the last rendered pose
        Assert.Equal(rendered, entity.GetMatrixPrevious());
        Assert.Equal(3.0f, entity.GetMatrix().Translation.X);

        Entity.CommitTransformHistory();
        Assert.Equal(entity.GetMatrix(), entity.GetMatrixPrevious()); // the epoch ended, nothing moved since

        // children capture on inherited changes
        var child = new Entity();
        child.SetParent(entity);
        child.SetPositionLocal(new Vector3(0, 1, 0));
        Entity.CommitTransformHistory();
        Matrix4x4 child_rendered = child.GetMatrix();
        entity.SetPosition(new Vector3(5, 0, 0));
        Assert.Equal(child_rendered, child.GetMatrixPrevious());
        Assert.Equal(5.0f, child.GetMatrix().Translation.X);
    }
}
