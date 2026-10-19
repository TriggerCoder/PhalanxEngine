using Phalanx.World;
using Xunit;

namespace Phalanx.Tests;

// Material.Destroy, engine owned materials and a failed preparation (the renderer side: RendererMaterialFallbackTests)
public class MaterialOwnershipTests
{
    [Fact]
    public void Destroy_drops_the_textures_and_marks_the_material()
    {
        var material = new Material();
        material.SetTexture(MaterialTextureType.Color, new RHI_Texture(4, 4));
        Assert.True(material.Destroy());
        Assert.True(material.IsDestroyed);
        Assert.Null(material.GetTexture(MaterialTextureType.Color));
    }

    [Fact]
    public void An_engine_owned_material_refuses_edits_and_destruction()
    {
        var material = new Material();
        material.SetProperty(MaterialProperty.ColorR, 0.25f);
        material.SetEngineOwned();
        Assert.True(material.IsEngineOwned);
        Assert.False(material.Destroy());
        Assert.False(material.IsDestroyed);
        material.SetProperty(MaterialProperty.ColorR, 0.75f);
        material.SetTexture(MaterialTextureType.Color, new RHI_Texture(4, 4));
        Assert.Equal(0.25f, material.GetProperty(MaterialProperty.ColorR));
        Assert.Null(material.GetTexture(MaterialTextureType.Color));
        // the derived dimensions a render writes when it binds the material stay writable
        material.SetProperty(MaterialProperty.WorldWidth, 2.0f);
        Assert.Equal(2.0f, material.GetProperty(MaterialProperty.WorldWidth));
    }

    [Fact]
    public void A_preparation_that_throws_marks_the_material_failed_until_one_succeeds()
    {
        var material = new Material();
        bool fail = true;
        Material.PrepareFaultForTests = m => fail && ReferenceEquals(m, material);
        try
        {
            material.PrepareForGpu();
            Assert.True(material.CreationFailed);
            fail = false;
            material.PrepareForGpu(); // still needs the repack, so it runs again
            Assert.False(material.CreationFailed);
        }
        finally
        {
            Material.PrepareFaultForTests = null;
        }
    }
}
