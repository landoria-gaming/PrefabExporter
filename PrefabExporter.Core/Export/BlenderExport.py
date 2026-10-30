# Builds an FBX from extracted meshes and prefab transforms.
import json
import os
import re
import sys
import tempfile
import bpy
import numpy as np
from mathutils import Matrix, Quaternion, Vector

# Builds a Blender material from extracted color and normal maps.
def material_for(material, base):
    material_name = material["Name"]
    result = bpy.data.materials.get(material_name) or bpy.data.materials.new(material_name)
    result.use_nodes = True
    nodes = result.node_tree.nodes
    shader = nodes.get("Principled BSDF")
    if material.get("Albedo"):
        texture = nodes.new("ShaderNodeTexImage")
        texture.interpolation = "Closest"
        texture.image = bpy.data.images.load(os.path.join(base, material["Albedo"]), check_existing=True)
        apply_uv_transform(result, texture, material.get("AlbedoSlot"))
        result.node_tree.links.new(texture.outputs["Color"], shader.inputs["Base Color"])
        result.node_tree.links.new(texture.outputs["Alpha"], shader.inputs["Alpha"])
    if material.get("Normal"):
        texture = nodes.new("ShaderNodeTexImage")
        texture.interpolation = "Closest"
        texture.image = bpy.data.images.load(os.path.join(base, material["Normal"]), check_existing=True)
        apply_uv_transform(result, texture, material.get("NormalSlot"))
        texture.image.colorspace_settings.name = "Non-Color"
        normal = nodes.new("ShaderNodeNormalMap")
        result.node_tree.links.new(texture.outputs["Color"], normal.inputs["Color"])
        result.node_tree.links.new(normal.outputs["Normal"], shader.inputs["Normal"])
    return result

# Applies the Unity material's UV scale and offset to a texture.
def apply_uv_transform(material, texture, slot):
    if not slot:
        return
    nodes = material.node_tree.nodes
    uv = nodes.new("ShaderNodeTexCoord")
    mapping = nodes.new("ShaderNodeMapping")
    mapping.inputs["Scale"].default_value = (*slot["Scale"], 1)
    mapping.inputs["Location"].default_value = (*slot["Offset"], 0)
    material.node_tree.links.new(uv.outputs["UV"], mapping.inputs["Vector"])
    material.node_tree.links.new(mapping.outputs["Vector"], texture.inputs["Vector"])

# Converts a Unity local transform into Blender coordinates.
def transform_for(node):
    conversion = Matrix(((-1, 0, 0, 0), (0, 0, -1, 0), (0, 1, 0, 0), (0, 0, 0, 1)))
    rotation = node["Rotation"]
    matrix = Matrix.LocRotScale(Vector(node["Position"]),
        Quaternion((rotation[3], rotation[0], rotation[1], rotation[2])), Vector(node["Scale"]))
    return conversion @ matrix @ conversion.inverted()

# Imports a mesh and attaches its materials under the prefab transform.
def attach_mesh(node, parent, base):
    if not node.get("Mesh"):
        return
    old = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=os.path.join(base, node["Mesh"]))
    imported = [obj for obj in bpy.data.objects if obj not in old]
    for obj in imported:
        if obj.parent is None:
            obj.parent = parent
        if obj.type == "MESH":
            match = re.search(r"SubMesh_(\d+)", obj.name)
            index = int(match.group(1)) if match else 0
            materials = node["Materials"]
            if materials:
                obj.data.materials.clear()
                obj.data.materials.append(material_for(materials[min(index, len(materials) - 1)], base))

# Bakes the albedo mapping into mesh UVs so FBX importers retain it.
def bake_fbx_uvs():
    mappings = set()
    for obj in bpy.data.objects:
        if obj.type != "MESH" or not obj.data.uv_layers.active:
            continue
        obj.data = obj.data.copy()
        uvs = obj.data.uv_layers.active.data
        normal_uv = obj.data.uv_layers.new(name="NormalUV").data
        for polygon in obj.data.polygons:
            material = obj.data.materials[polygon.material_index] if obj.data.materials else None
            mapping = albedo_mapping(material)
            normal_mapping = normal_texture_mapping(material)
            if normal_mapping:
                normal_scale = normal_mapping.inputs["Scale"].default_value
                normal_offset = normal_mapping.inputs["Location"].default_value
                for index in polygon.loop_indices:
                    uv = uvs[index].uv
                    normal_uv[index].uv = (uv.x * normal_scale.x + normal_offset.x,
                        uv.y * normal_scale.y + normal_offset.y)
            if mapping is None:
                continue
            mappings.add(mapping)
            scale = mapping.inputs["Scale"].default_value
            offset = mapping.inputs["Location"].default_value
            for index in polygon.loop_indices:
                uv = uvs[index].uv
                uv.x = uv.x * scale.x + offset.x
                uv.y = uv.y * scale.y + offset.y
    for mapping in mappings:
        mapping.inputs["Scale"].default_value = (1, 1, 1)
        mapping.inputs["Location"].default_value = (0, 0, 0)

# Finds the mapping node connected to the normal texture.
def normal_texture_mapping(material):
    if not material or not material.use_nodes:
        return None
    shader = material.node_tree.nodes.get("Principled BSDF")
    if not shader or not shader.inputs["Normal"].is_linked:
        return None
    normal = shader.inputs["Normal"].links[0].from_node
    if normal.type != "NORMAL_MAP" or not normal.inputs["Color"].is_linked:
        return None
    texture = normal.inputs["Color"].links[0].from_node
    if texture.type != "TEX_IMAGE" or not texture.inputs["Vector"].is_linked:
        return None
    mapping = texture.inputs["Vector"].links[0].from_node
    return mapping if mapping.type == "MAPPING" else None

# Finds the mapping node connected to a material's albedo texture.
def albedo_mapping(material):
    if not material or not material.use_nodes:
        return None
    shader = material.node_tree.nodes.get("Principled BSDF")
    if not shader or not shader.inputs["Base Color"].is_linked:
        return None
    texture = shader.inputs["Base Color"].links[0].from_node
    if texture.type != "TEX_IMAGE" or not texture.inputs["Vector"].is_linked:
        return None
    node = texture.inputs["Vector"].links[0].from_node
    return node if node.type == "MAPPING" else None

# Builds an independent scene from the extracted prefab resources.
def build_scene(plan):
    base = os.path.dirname(plan["Output"])
    bpy.ops.wm.read_factory_settings(use_empty=True)
    objects = []
    for node in plan["Nodes"]:
        holder = bpy.data.objects.new(node["Name"], None)
        bpy.context.scene.collection.objects.link(holder)
        if node["Parent"] >= 0:
            holder.parent = objects[node["Parent"]]
        holder.matrix_local = transform_for(node)
        objects.append(holder)
        attach_mesh(node, holder, base)

# Enlarges color pixels for importers that always use linear filtering.
def sharpen_fbx_textures(directory):
    images = {}
    for material in bpy.data.materials:
        if not material.use_nodes:
            continue
        shader = material.node_tree.nodes.get("Principled BSDF")
        if not shader or not shader.inputs["Base Color"].is_linked:
            continue
        node = shader.inputs["Base Color"].links[0].from_node
        if node.type != "TEX_IMAGE" or not node.image:
            continue
        source = node.image
        if source not in images:
            images[source] = enlarge_image(source, directory, len(images))
        node.image = images[source]

# Copies image pixels with nearest-neighbor scaling for FBX embedding.
def enlarge_image(source, directory, index):
    width, height = source.size
    factor = min(8, 4096 // max(width, height))
    if factor < 2:
        return source
    pixels = np.empty(width * height * 4, dtype=np.float32)
    source.pixels.foreach_get(pixels)
    pixels = np.repeat(np.repeat(pixels.reshape(height, width, 4), factor, axis=0), factor, axis=1)
    result = bpy.data.images.new(source.name + "_fbx", width=width * factor, height=height * factor, alpha=True)
    result.colorspace_settings.name = source.colorspace_settings.name
    result.pixels.foreach_set(pixels.ravel())
    result.filepath_raw = os.path.join(directory, f"texture-{index}.png")
    result.file_format = "PNG"
    result.save()
    return result

# Creates the FBX file from the extracted scene.
def main():
    manifest = sys.argv[sys.argv.index("--") + 1]
    with open(manifest, encoding="utf-8") as source:
        plan = json.load(source)
    build_scene(plan)
    bake_fbx_uvs()
    with tempfile.TemporaryDirectory(prefix="prefab-fbx-textures-") as directory:
        sharpen_fbx_textures(directory)
        bpy.ops.export_scene.fbx(filepath=plan["Output"], path_mode="COPY", embed_textures=True,
            object_types={"EMPTY", "MESH"}, bake_anim=False, add_leaf_bones=False)

main()
