using System.Numerics;

namespace Phalanx;

// TODO(port): source/resource/import/ModelImporter.cpp (assimp scene loading, nodes, lights, skeletons, animations,
// texture binding). only the material-name rules and the per-mesh AddGeometry call site are ported so far; they're
// what decides the foliage/impostor geometry path
public static class ModelImporter
{
    // the naming rule Spartan uses everywhere for leaf geometry: case-sensitive "_foliage" anywhere in the material
    // name. alpha masks alone are not sufficient here: fences and decals use them too
    public const string FoliageMaterialTag = "_foliage";

    public static bool IsFoliageMaterialName(string? material_name)
    {
        return material_name != null && material_name.Contains(FoliageMaterialTag, StringComparison.Ordinal);
    }

    // load_material(): transparency guessed from the (lowercased) name when the material has full opacity
    public static bool IsTransparentMaterialName(string? material_name)
    {
        if (material_name == null)
            return false;
        string name_lower = material_name.ToLowerInvariant();
        return name_lower.Contains("glass", StringComparison.Ordinal) ||
               name_lower.Contains("transparent", StringComparison.Ordinal) ||
               name_lower.Contains("bottle", StringComparison.Ordinal);
    }

    // load_material(): explicitly named leaf geometry also works when placed outside terrain scatter
    public static void ApplyMaterialNameRules(Material material, string material_name)
    {
        if (IsFoliageMaterialName(material_name))
        {
            material.SetProperty(MaterialProperty.IsFoliage, 1.0f);
            material.SetProperty(MaterialProperty.SubsurfaceScattering, 0.35f);
            material.SetProperty(MaterialProperty.CullMode, (float)RHI_CullMode.None);
        }
        // TODO(port): the rest of load_material (color + alpha mask -> CullMode None, gltf flag, base color, opacity,
        // the name based transparency (IsTransparentMaterialName) -> opacity 0.5 / roughness 0 for non pbr materials, ...)
    }

    // ParseMesh(): leaf cards come from "_foliage" materials. the trunk and branches of a plant switch to their own
    // card together with the crown (impostor), left as meshes they stay on their coarsest lod out to the scatter
    // distance and cost more than the whole forest canopy, so every mesh of a scene with any foliage material is a plant
    public static (bool foliage_cards, bool plant) GetFoliageFlags(IReadOnlyList<string> scene_material_names, int mesh_material_index)
    {
        bool foliage_cards = (uint)mesh_material_index < (uint)scene_material_names.Count &&
                             IsFoliageMaterialName(scene_material_names[mesh_material_index]);

        bool plant = foliage_cards;
        for (int i = 0; i < scene_material_names.Count && !plant; i++)
            plant = IsFoliageMaterialName(scene_material_names[i]);

        return (foliage_cards, plant);
    }

    // the AddGeometry call at the end of ParseMesh(), vertices/indices come from the (not yet ported) assimp mesh
    // processing. lods only for unskinned meshes (skin weights keep assimp vertex order), lod 0 always preserved
    public static void AddMeshGeometry(Mesh mesh, List<RHI_Vertex_PosTexNorTan> vertices, List<uint> indices, int sub_mesh_index,
                                       IReadOnlyList<string> scene_material_names, int mesh_material_index)
    {
        var (foliage_cards, plant) = GetFoliageFlags(scene_material_names, mesh_material_index);
        mesh.ReserveSubMeshes(sub_mesh_index + 1);
        mesh.AddGeometry(vertices, indices, mesh.GetSkeleton() == null, sub_mesh_index, preserve_lod0: true, foliage_cards: foliage_cards, impostor: plant);
    }
}
