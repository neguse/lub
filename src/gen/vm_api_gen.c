/* Generated from lub_stub.cs by lub-gen vm (the tcs VM host functions). Do not edit. */
#include "tcsvm.h"
#include <lub/lub_api.h>
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#if defined(__GNUC__) || defined(__clang__)
#define VM_LUB_UNUSED __attribute__((unused))
#else
#define VM_LUB_UNUSED
#endif
typedef struct VmLubOptI32 {
  bool has;
  int32_t v;
} VmLubOptI32;
typedef struct VmLubOptF32 {
  bool has;
  float v;
} VmLubOptF32;
typedef struct VmLubOptBool {
  bool has;
  bool v;
} VmLubOptBool;
static LubContext *vm_lub_context;
static TvmVm *vm_lub_vm;
static const char *vm_lub_error;
static char vm_lub_error_text[1024];
static void **vm_lub_temps;
static size_t vm_lub_ntemps, vm_lub_ctemps;
static void vm_lub_fail(const char *message) {
  if (!vm_lub_error)
    vm_lub_error = message;
}
static void vm_lub_fail_text(const char *message) {
  if (vm_lub_error)
    return;
  snprintf(vm_lub_error_text, sizeof vm_lub_error_text, "%s", message);
  vm_lub_error = vm_lub_error_text;
}
/* callback の中の VM の fault (trace は tvm_error) */
static void vm_lub_fail_vm(void) { vm_lub_fail_text(tvm_error(vm_lub_vm)); }
static void vm_lub_check(LubStatus status) {
  if (status == LUB_ERROR) {
    if (vm_lub_error)
      return;
    snprintf(vm_lub_error_text, sizeof vm_lub_error_text, "lub-api: %s",
             lub_last_error(vm_lub_context));
    vm_lub_error = vm_lub_error_text;
  }
}
/* host の関数の間だけの C の memory (入力の record の list など) */
static void *vm_lub_alloc(size_t size) {
  if (vm_lub_ntemps == vm_lub_ctemps) {
    size_t cap = vm_lub_ctemps ? vm_lub_ctemps * 2 : 16;
    void **grown = realloc(vm_lub_temps, cap * sizeof(void *));
    if (!grown)
      abort();
    vm_lub_temps = grown;
    vm_lub_ctemps = cap;
  }
  void *p = calloc(1, size ? size : 1);
  if (!p)
    abort();
  vm_lub_temps[vm_lub_ntemps++] = p;
  return p;
}
static void vm_lub_free_to(size_t mark) {
  while (vm_lub_ntemps > mark)
    free(vm_lub_temps[--vm_lub_ntemps]);
}
#define VM_LUB_ENTER()                                                         \
  size_t vm_lub_mark = vm_lub_ntemps;                                          \
  const char *vm_lub_outer = vm_lub_error;                                     \
  vm_lub_error = NULL
#define VM_LUB_LEAVE(vm) return vm_lub_leave(vm, vm_lub_mark, vm_lub_outer)
static TvmStatus vm_lub_leave(TvmVm *vm, size_t mark, const char *outer) {
  vm_lub_free_to(mark);
  const char *error = vm_lub_error;
  vm_lub_error = outer;
  return error ? tvm_host_fault(vm, error) : TVM_OK;
}
/* VM の型 (読み込みと reload で作り直すので版で引き直す) */
typedef struct VmLubType {
  const char *name;
  uint32_t version;
  const TvmTypeInfo *type;
} VmLubType;
static const TvmTypeInfo *vm_lub_type(VmLubType *t) {
  uint32_t version = tvm_types_version(vm_lub_vm);
  if (t->version != version) {
    t->type = tvm_type(vm_lub_vm, t->name);
    t->version = version;
  }
  if (!t->type && !vm_lub_error) {
    snprintf(vm_lub_error_text, sizeof vm_lub_error_text,
             "lub: the module has no type %s", t->name);
    vm_lub_error = vm_lub_error_text;
  }
  return t->type;
}
/* 型が無ければ (誤りは積んである) 捨てる C の memory を返し、呼び出し側の書き込みを受ける */
static void *vm_lub_new(VmLubType *t, size_t size) {
  const TvmTypeInfo *type = vm_lub_type(t);
  void *o = type ? tvm_object_new_of(vm_lub_vm, type) : NULL;
  return o ? o : vm_lub_alloc(size);
}
static void *vm_lub_list_new(VmLubType *t) {
  const TvmTypeInfo *type = vm_lub_type(t);
  return type ? tvm_list_new_of(vm_lub_vm, type, 0) : NULL;
}
static void *vm_lub_array(VmLubType *t, int32_t len) {
  const TvmTypeInfo *type = vm_lub_type(t);
  return type ? tvm_array_new_of(vm_lub_vm, type, len) : NULL;
}
/* keyed の object の key (host_value は表の番号 + 1。key は少なく、表は process の間残す) */
static char **vm_lub_keys;
static uint32_t vm_lub_nkeys;
static LubStr vm_lub_key_load(uint64_t host_value) {
  if (host_value == 0 || host_value > vm_lub_nkeys) {
    vm_lub_fail("null reference");
    return (LubStr){0};
  }
  const char *key = vm_lub_keys[host_value - 1];
  return (LubStr){key, (int32_t)strlen(key)};
}
static uint64_t vm_lub_key_store(LubStr key) {
  for (uint32_t i = 0; i < vm_lub_nkeys; i++)
    if ((int32_t)strlen(vm_lub_keys[i]) == key.len &&
        memcmp(vm_lub_keys[i], key.ptr, (size_t)key.len) == 0)
      return i + 1;
  char **grown = realloc(vm_lub_keys, (vm_lub_nkeys + 1) * sizeof(char *));
  char *copy = malloc((size_t)key.len + 1);
  if (!grown || !copy)
    abort();
  memcpy(copy, key.ptr, (size_t)key.len);
  copy[key.len] = 0;
  vm_lub_keys = grown;
  vm_lub_keys[vm_lub_nkeys++] = copy;
  return vm_lub_nkeys;
}
static LubStr vm_lub_str(void *text) {
  size_t len = 0;
  const char *data = tvm_string_data(text, &len);
  return data ? (LubStr){data, (int32_t)len} : (LubStr){0};
}
static void *vm_lub_string(LubStr text) {
  return text.ptr ? tvm_string_new(vm_lub_vm, text.ptr, (size_t)text.len)
                  : NULL;
}
static void *vm_lub_list(VmLubType *t, void *list, const void *data,
                         int32_t count, size_t size) {
  if (!list)
    list = vm_lub_list_new(t);
  if (!list)
    return NULL;
  if (!tvm_list_resize(vm_lub_vm, list, count)) {
    vm_lub_fail("allocation-overflow");
    return list;
  }
  if (count)
    memcpy(tvm_list_data(list), data, (size_t)count * size);
  return list;
}
typedef struct VmLub_Bindings VmLub_Bindings;
typedef struct VmLub_TextureRef VmLub_TextureRef;
typedef struct VmLub_ShaderRef VmLub_ShaderRef;
typedef struct VmLub_BufferRef VmLub_BufferRef;
typedef struct VmLub_FrameRange VmLub_FrameRange;
typedef struct VmLub_Bytes VmLub_Bytes;
typedef struct VmLub_PassOpts VmLub_PassOpts;
typedef struct VmLub_DrawOpts VmLub_DrawOpts;
typedef struct VmLub_DispatchOpts VmLub_DispatchOpts;
typedef struct VmLub_TextureOpts VmLub_TextureOpts;
typedef struct VmLub_Readback VmLub_Readback;
typedef struct VmLub_XrView VmLub_XrView;
typedef struct VmLub_XrInput VmLub_XrInput;
typedef struct VmLub_ConfigOpts VmLub_ConfigOpts;
typedef struct VmLub_MeshData VmLub_MeshData;
typedef struct VmLub_SdfBone VmLub_SdfBone;
typedef struct VmLub_SdfNodeDesc VmLub_SdfNodeDesc;
typedef struct VmLub_GltfMaterial VmLub_GltfMaterial;
typedef struct VmLub_GltfPrimitive VmLub_GltfPrimitive;
typedef struct VmLub_GltfMesh VmLub_GltfMesh;
typedef struct VmLub_GlyphBitmap VmLub_GlyphBitmap;
typedef struct VmLub_GlyphMesh VmLub_GlyphMesh;
typedef struct VmLub_FontMetrics VmLub_FontMetrics;
typedef struct VmLub_PlayOpts VmLub_PlayOpts;
typedef struct VmLub_VoiceOpts VmLub_VoiceOpts;
typedef struct VmLub_AudioInfo VmLub_AudioInfo;
typedef struct VmLub_Vec2d VmLub_Vec2d;
typedef struct VmLub_WorldRef VmLub_WorldRef;
typedef struct VmLub_BodyRef VmLub_BodyRef;
typedef struct VmLub_ShapeRef VmLub_ShapeRef;
typedef struct VmLub_ChainRef VmLub_ChainRef;
typedef struct VmLub_JointRef VmLub_JointRef;
typedef struct VmLub_InitialState VmLub_InitialState;
typedef struct VmLub_ShapeView VmLub_ShapeView;
typedef struct VmLub_MaterialView VmLub_MaterialView;
typedef struct VmLub_ManifoldPoint VmLub_ManifoldPoint;
typedef struct VmLub_PreSolveContact VmLub_PreSolveContact;
typedef struct VmLub_WorldCallbacks VmLub_WorldCallbacks;
typedef struct VmLub_WorldOpts VmLub_WorldOpts;
typedef struct VmLub_BeginOpts VmLub_BeginOpts;
typedef struct VmLub_BodyDesc VmLub_BodyDesc;
typedef struct VmLub_FilterDesc VmLub_FilterDesc;
typedef struct VmLub_ShapeDesc VmLub_ShapeDesc;
typedef struct VmLub_BoxDesc VmLub_BoxDesc;
typedef struct VmLub_CircleDesc VmLub_CircleDesc;
typedef struct VmLub_CapsuleDesc VmLub_CapsuleDesc;
typedef struct VmLub_SegmentDesc VmLub_SegmentDesc;
typedef struct VmLub_PolygonDesc VmLub_PolygonDesc;
typedef struct VmLub_ChainMaterial VmLub_ChainMaterial;
typedef struct VmLub_ChainDesc VmLub_ChainDesc;
typedef struct VmLub_JointSpringDesc VmLub_JointSpringDesc;
typedef struct VmLub_JointLimitDesc VmLub_JointLimitDesc;
typedef struct VmLub_JointMotorDesc VmLub_JointMotorDesc;
typedef struct VmLub_JointTargetDesc VmLub_JointTargetDesc;
typedef struct VmLub_JointDesc VmLub_JointDesc;
typedef struct VmLub_CommandOpts VmLub_CommandOpts;
typedef struct VmLub_VelocityDesc VmLub_VelocityDesc;
typedef struct VmLub_PoseDesc VmLub_PoseDesc;
typedef struct VmLub_MassDataDesc VmLub_MassDataDesc;
typedef struct VmLub_MaterialDesc VmLub_MaterialDesc;
typedef struct VmLub_ShapeEventsDesc VmLub_ShapeEventsDesc;
typedef struct VmLub_RaycastDesc VmLub_RaycastDesc;
typedef struct VmLub_AabbDesc VmLub_AabbDesc;
typedef struct VmLub_ShapeCastDesc VmLub_ShapeCastDesc;
typedef struct VmLub_MoverDesc VmLub_MoverDesc;
typedef struct VmLub_ExplosionDesc VmLub_ExplosionDesc;
typedef struct VmLub_DebugOpts VmLub_DebugOpts;
typedef struct VmLub_DebugData VmLub_DebugData;
typedef struct VmLub_Pose VmLub_Pose;
typedef struct VmLub_Velocity VmLub_Velocity;
typedef struct VmLub_MassData VmLub_MassData;
typedef struct VmLub_Aabb VmLub_Aabb;
typedef struct VmLub_FilterInfo VmLub_FilterInfo;
typedef struct VmLub_ShapeInfo VmLub_ShapeInfo;
typedef struct VmLub_WorldCallbackInfo VmLub_WorldCallbackInfo;
typedef struct VmLub_WorldInfo VmLub_WorldInfo;
typedef struct VmLub_StepInfo VmLub_StepInfo;
typedef struct VmLub_JointView VmLub_JointView;
typedef struct VmLub_JointInfo VmLub_JointInfo;
typedef struct VmLub_ContactData VmLub_ContactData;
typedef struct VmLub_ContactEvent VmLub_ContactEvent;
typedef struct VmLub_SensorEvent VmLub_SensorEvent;
typedef struct VmLub_BodyEvent VmLub_BodyEvent;
typedef struct VmLub_RayHit VmLub_RayHit;
typedef struct VmLub_ShapeRayHit VmLub_ShapeRayHit;
typedef struct VmLub_MoverCast VmLub_MoverCast;
typedef struct VmLub_MoverPlane VmLub_MoverPlane;
typedef struct VmLub_Profile VmLub_Profile;
typedef struct VmLub_Counters VmLub_Counters;
typedef struct VmLub_Vec3d VmLub_Vec3d;
typedef struct VmLub_Quat3d VmLub_Quat3d;
typedef struct VmLub_WorldRef3d VmLub_WorldRef3d;
typedef struct VmLub_BodyRef3d VmLub_BodyRef3d;
typedef struct VmLub_ShapeRef3d VmLub_ShapeRef3d;
typedef struct VmLub_JointRef3d VmLub_JointRef3d;
typedef struct VmLub_InitialState3d VmLub_InitialState3d;
typedef struct VmLub_MotionLocks3d VmLub_MotionLocks3d;
typedef struct VmLub_ShapeView3d VmLub_ShapeView3d;
typedef struct VmLub_PreSolveContact3d VmLub_PreSolveContact3d;
typedef struct VmLub_WorldCallbacks3d VmLub_WorldCallbacks3d;
typedef struct VmLub_WorldOpts3d VmLub_WorldOpts3d;
typedef struct VmLub_BeginOpts3d VmLub_BeginOpts3d;
typedef struct VmLub_BodyDesc3d VmLub_BodyDesc3d;
typedef struct VmLub_FilterDesc3d VmLub_FilterDesc3d;
typedef struct VmLub_ShapeDesc3d VmLub_ShapeDesc3d;
typedef struct VmLub_SphereDesc3d VmLub_SphereDesc3d;
typedef struct VmLub_BoxDesc3d VmLub_BoxDesc3d;
typedef struct VmLub_CapsuleDesc3d VmLub_CapsuleDesc3d;
typedef struct VmLub_CylinderDesc3d VmLub_CylinderDesc3d;
typedef struct VmLub_ConeDesc3d VmLub_ConeDesc3d;
typedef struct VmLub_HullDesc3d VmLub_HullDesc3d;
typedef struct VmLub_SurfaceMaterial3d VmLub_SurfaceMaterial3d;
typedef struct VmLub_MeshDesc3d VmLub_MeshDesc3d;
typedef struct VmLub_HeightFieldDesc3d VmLub_HeightFieldDesc3d;
typedef struct VmLub_CompoundSphere3d VmLub_CompoundSphere3d;
typedef struct VmLub_CompoundBox3d VmLub_CompoundBox3d;
typedef struct VmLub_CompoundCapsule3d VmLub_CompoundCapsule3d;
typedef struct VmLub_CompoundChild3d VmLub_CompoundChild3d;
typedef struct VmLub_CompoundDesc3d VmLub_CompoundDesc3d;
typedef struct VmLub_CommandOpts3d VmLub_CommandOpts3d;
typedef struct VmLub_VelocityDesc3d VmLub_VelocityDesc3d;
typedef struct VmLub_PoseDesc3d VmLub_PoseDesc3d;
typedef struct VmLub_TargetDesc3d VmLub_TargetDesc3d;
typedef struct VmLub_FrameDesc3d VmLub_FrameDesc3d;
typedef struct VmLub_JointSpringDesc3d VmLub_JointSpringDesc3d;
typedef struct VmLub_JointLimitDesc3d VmLub_JointLimitDesc3d;
typedef struct VmLub_JointMotorDesc3d VmLub_JointMotorDesc3d;
typedef struct VmLub_JointTargetDesc3d VmLub_JointTargetDesc3d;
typedef struct VmLub_JointDesc3d VmLub_JointDesc3d;
typedef struct VmLub_MaterialDesc3d VmLub_MaterialDesc3d;
typedef struct VmLub_ShapeEventsDesc3d VmLub_ShapeEventsDesc3d;
typedef struct VmLub_MoverDesc3d VmLub_MoverDesc3d;
typedef struct VmLub_RaycastDesc3d VmLub_RaycastDesc3d;
typedef struct VmLub_AabbDesc3d VmLub_AabbDesc3d;
typedef struct VmLub_SphereProxy3d VmLub_SphereProxy3d;
typedef struct VmLub_BoxProxy3d VmLub_BoxProxy3d;
typedef struct VmLub_CapsuleProxy3d VmLub_CapsuleProxy3d;
typedef struct VmLub_ShapeProxyDesc3d VmLub_ShapeProxyDesc3d;
typedef struct VmLub_Pose3d VmLub_Pose3d;
typedef struct VmLub_Velocity3d VmLub_Velocity3d;
typedef struct VmLub_Inertia3d VmLub_Inertia3d;
typedef struct VmLub_MassData3d VmLub_MassData3d;
typedef struct VmLub_Aabb3d VmLub_Aabb3d;
typedef struct VmLub_ShapeInfo3d VmLub_ShapeInfo3d;
typedef struct VmLub_WorldInfo3d VmLub_WorldInfo3d;
typedef struct VmLub_StepInfo3d VmLub_StepInfo3d;
typedef struct VmLub_Frame3d VmLub_Frame3d;
typedef struct VmLub_JointView3d VmLub_JointView3d;
typedef struct VmLub_JointInfo3d VmLub_JointInfo3d;
typedef struct VmLub_ContactData3d VmLub_ContactData3d;
typedef struct VmLub_ContactEvent3d VmLub_ContactEvent3d;
typedef struct VmLub_SensorEvent3d VmLub_SensorEvent3d;
typedef struct VmLub_BodyEvent3d VmLub_BodyEvent3d;
typedef struct VmLub_JointEvent3d VmLub_JointEvent3d;
typedef struct VmLub_RayHit3d VmLub_RayHit3d;
typedef struct VmLub_ShapeRayHit3d VmLub_ShapeRayHit3d;
typedef struct VmLub_MoverCast3d VmLub_MoverCast3d;
typedef struct VmLub_MoverPlane3d VmLub_MoverPlane3d;
typedef struct VmLub_Profile3d VmLub_Profile3d;
typedef struct VmLub_Counters3d VmLub_Counters3d;
typedef struct VmLub_EventData VmLub_EventData;
struct VmLub_Bindings {
  uint32_t type_id;
  uint64_t host_value;
  void *f_blob;
};
struct VmLub_TextureRef {
  uint32_t type_id;
  uint64_t host_value;
  int32_t f_version;
};
struct VmLub_ShaderRef {
  uint32_t type_id;
  uint64_t host_value;
  int32_t f_version;
};
struct VmLub_BufferRef {
  uint32_t type_id;
  uint64_t host_value;
  int32_t f_version;
};
struct VmLub_FrameRange {
  uint32_t type_id;
  uint64_t host_value;
};
struct VmLub_Bytes {
  uint32_t type_id;
  uint64_t host_value;
  int32_t f_length;
};
struct VmLub_PassOpts {
  uint32_t type_id;
  uint64_t host_value;
  VmLub_TextureRef *f_target;
  void *f_targets;
  VmLub_TextureRef *f_depth_target;
  void *f_clear_color;
  void *f_clear_colors;
  VmLubOptF32 f_clear_depth;
  VmLubOptI32 f_load;
};
struct VmLub_DrawOpts {
  uint32_t type_id;
  uint64_t host_value;
  VmLub_ShaderRef *f_shader;
  VmLubOptI32 f_blend;
  VmLubOptI32 f_cull;
  VmLubOptI32 f_primitive;
  VmLubOptBool f_depth;
  VmLubOptBool f_depth_write;
};
struct VmLub_DispatchOpts {
  uint32_t type_id;
  uint64_t host_value;
  VmLub_ShaderRef *f_shader;
};
struct VmLub_TextureOpts {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptI32 f_filter;
  VmLubOptI32 f_wrap;
  VmLubOptBool f_target;
  VmLubOptBool f_storage;
};
struct VmLub_Readback {
  uint32_t type_id;
  uint64_t host_value;
};
struct VmLub_XrView {
  uint32_t type_id;
  uint64_t host_value;
  VmLub_TextureRef *f_target;
  int32_t f_width;
  int32_t f_height;
  void *f_position;
  void *f_orientation;
  void *f_view_projection;
};
struct VmLub_XrInput {
  uint32_t type_id;
  uint64_t host_value;
  bool f_active;
  float f_stick_x;
  float f_stick_y;
  float f_trigger;
  float f_grip;
  bool f_primary;
  bool f_secondary;
  bool f_menu;
  bool f_stick_click;
};
struct VmLub_ConfigOpts {
  uint32_t type_id;
  uint64_t host_value;
  void *f_backend;
  VmLubOptI32 f_width;
  VmLubOptI32 f_height;
  VmLubOptI32 f_resource_sweep_after_frames;
  VmLubOptI32 f_readback_depth;
  VmLubOptBool f_readback_fixed_latency;
};
struct VmLub_MeshData {
  uint32_t type_id;
  uint64_t host_value;
  void *f_positions;
  void *f_normals;
  void *f_indices;
  int32_t f_vert_count;
  int32_t f_index_count;
  void *f_uvs;
  void *f_tangents;
  void *f_bounds_min;
  void *f_bounds_max;
  VmLubOptF32 f_cell;
  void *f_colors;
  void *f_metal_rough;
  void *f_joints;
  void *f_weights;
  void *f_bones;
};
struct VmLub_SdfBone {
  uint32_t type_id;
  uint64_t host_value;
  void *f_name;
  float f_x;
  float f_y;
  float f_z;
};
struct VmLub_SdfNodeDesc {
  uint32_t type_id;
  uint64_t host_value;
  int32_t f_op;
  int32_t f_a;
  int32_t f_b;
  void *f_params;
  void *f_name;
};
struct VmLub_GltfMaterial {
  uint32_t type_id;
  uint64_t host_value;
  void *f_base_color_factor;
  float f_metallic_factor;
  float f_roughness_factor;
  int32_t f_alpha_mode;
  float f_alpha_cutoff;
  bool f_double_sided;
  float f_normal_scale;
  void *f_base_color_path;
  void *f_metallic_roughness_path;
  void *f_normal_path;
  void *f_name;
};
struct VmLub_GltfPrimitive {
  uint32_t type_id;
  uint64_t host_value;
  void *f_positions;
  void *f_normals;
  void *f_indices;
  int32_t f_vert_count;
  int32_t f_index_count;
  void *f_uvs;
  void *f_tangents;
  void *f_bounds_min;
  void *f_bounds_max;
  VmLubOptF32 f_cell;
  void *f_colors;
  void *f_metal_rough;
  void *f_joints;
  void *f_weights;
  void *f_bones;
  int32_t f_material_index;
  VmLub_GltfMaterial *f_material;
};
struct VmLub_GltfMesh {
  uint32_t type_id;
  uint64_t host_value;
  void *f_positions;
  void *f_normals;
  void *f_indices;
  int32_t f_vert_count;
  int32_t f_index_count;
  void *f_uvs;
  void *f_tangents;
  void *f_bounds_min;
  void *f_bounds_max;
  VmLubOptF32 f_cell;
  void *f_colors;
  void *f_metal_rough;
  void *f_joints;
  void *f_weights;
  void *f_bones;
  void *f_primitives;
  VmLub_GltfMaterial *f_material;
};
struct VmLub_GlyphBitmap {
  uint32_t type_id;
  uint64_t host_value;
  int32_t f_w;
  int32_t f_h;
  int32_t f_xoff;
  int32_t f_yoff;
  float f_advance;
  VmLub_Bytes *f_bytes;
};
struct VmLub_GlyphMesh {
  uint32_t type_id;
  uint64_t host_value;
  void *f_positions;
  void *f_normals;
  void *f_indices;
  int32_t f_vert_count;
  int32_t f_index_count;
  void *f_uvs;
  void *f_tangents;
  void *f_bounds_min;
  void *f_bounds_max;
  VmLubOptF32 f_cell;
  void *f_colors;
  void *f_metal_rough;
  void *f_joints;
  void *f_weights;
  void *f_bones;
  float f_advance;
};
struct VmLub_FontMetrics {
  uint32_t type_id;
  uint64_t host_value;
  float f_ascent;
  float f_descent;
  float f_line_gap;
};
struct VmLub_PlayOpts {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptF32 f_volume;
  VmLubOptF32 f_pitch;
  VmLubOptF32 f_pan;
};
struct VmLub_VoiceOpts {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptF32 f_volume;
  VmLubOptF32 f_pitch;
  VmLubOptF32 f_pan;
  VmLubOptBool f_loop;
};
struct VmLub_AudioInfo {
  uint32_t type_id;
  uint64_t host_value;
  bool f_device;
  int32_t f_rate;
  int32_t f_voices;
  int32_t f_snds;
};
struct VmLub_Vec2d {
  uint32_t type_id;
  uint64_t host_value;
  float f_x;
  float f_y;
};
struct VmLub_WorldRef {
  uint32_t type_id;
  uint64_t host_value;
};
struct VmLub_BodyRef {
  uint32_t type_id;
  uint64_t host_value;
};
struct VmLub_ShapeRef {
  uint32_t type_id;
  uint64_t host_value;
};
struct VmLub_ChainRef {
  uint32_t type_id;
  uint64_t host_value;
};
struct VmLub_JointRef {
  uint32_t type_id;
  uint64_t host_value;
};
struct VmLub_InitialState {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptF32 f_x;
  VmLubOptF32 f_y;
  VmLubOptF32 f_angle;
  VmLubOptF32 f_vx;
  VmLubOptF32 f_vy;
  VmLubOptF32 f_w;
  VmLubOptBool f_awake;
};
struct VmLub_ShapeView {
  uint32_t type_id;
  uint64_t host_value;
  void *f_body;
  void *f_shape;
  void *f_tag;
  void *f_chain;
  VmLubOptBool f_segment;
  void *f_material_name;
  VmLubOptI32 f_material_id;
  VmLubOptI32 f_kind;
  void *f_category_bits;
  void *f_mask_bits;
  VmLubOptI32 f_group;
  bool f_valid;
};
struct VmLub_MaterialView {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptF32 f_friction;
  VmLubOptF32 f_restitution;
  int32_t f_material_id;
};
struct VmLub_ManifoldPoint {
  uint32_t type_id;
  uint64_t host_value;
  float f_x;
  float f_y;
  float f_anchor_a_x;
  float f_anchor_a_y;
  float f_anchor_b_x;
  float f_anchor_b_y;
  float f_separation;
  float f_normal_impulse;
  float f_tangent_impulse;
  float f_total_normal_impulse;
  float f_normal_velocity;
  int32_t f_id;
  bool f_persisted;
};
struct VmLub_PreSolveContact {
  uint32_t type_id;
  uint64_t host_value;
  VmLub_ShapeView *f_a;
  VmLub_ShapeView *f_b;
  float f_nx;
  float f_ny;
  float f_rolling_impulse;
  int32_t f_point_count;
  void *f_points;
  VmLubOptF32 f_x;
  VmLubOptF32 f_y;
  VmLubOptF32 f_separation;
  VmLubOptF32 f_normal_velocity;
};
struct VmLub_WorldCallbacks {
  uint32_t type_id;
  uint64_t host_value;
  void *f_filter;
  void *f_pre_solve;
  void *f_friction;
  void *f_restitution;
};
struct VmLub_WorldOpts {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptI32 f_version;
  VmLub_Vec2d *f_gravity;
  VmLubOptF32 f_fixed_dt;
  VmLubOptI32 f_substeps;
  VmLubOptI32 f_max_steps;
  VmLubOptBool f_sleep;
  VmLubOptBool f_continuous;
  VmLubOptF32 f_hit_event_threshold;
  VmLub_WorldCallbacks *f_callbacks;
};
struct VmLub_BeginOpts {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptBool f_prune;
};
struct VmLub_BodyDesc {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptI32 f_version;
  VmLubOptI32 f_type;
  VmLubOptBool f_fixed_rotation;
  VmLubOptBool f_bullet;
  VmLubOptBool f_enabled;
  VmLubOptBool f_awake;
  VmLubOptBool f_sleep;
  VmLubOptF32 f_sleep_threshold;
  VmLubOptF32 f_gravity_scale;
  VmLubOptF32 f_linear_damping;
  VmLubOptF32 f_angular_damping;
  VmLub_InitialState *f_initial;
};
struct VmLub_FilterDesc {
  uint32_t type_id;
  uint64_t host_value;
  void *f_category_bits;
  void *f_mask_bits;
  VmLubOptI32 f_group;
};
struct VmLub_ShapeDesc {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptI32 f_version;
  VmLubOptF32 f_density;
  VmLubOptF32 f_friction;
  VmLubOptF32 f_restitution;
  void *f_tag;
  void *f_material_name;
  VmLubOptI32 f_material_id;
  VmLubOptBool f_sensor;
  VmLubOptBool f_contact;
  VmLubOptBool f_hit;
  VmLubOptBool f_sensor_events;
  VmLubOptBool f_pre_solve;
  VmLub_FilterDesc *f_filter;
};
struct VmLub_BoxDesc {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptI32 f_version;
  VmLubOptF32 f_density;
  VmLubOptF32 f_friction;
  VmLubOptF32 f_restitution;
  void *f_tag;
  void *f_material_name;
  VmLubOptI32 f_material_id;
  VmLubOptBool f_sensor;
  VmLubOptBool f_contact;
  VmLubOptBool f_hit;
  VmLubOptBool f_sensor_events;
  VmLubOptBool f_pre_solve;
  VmLub_FilterDesc *f_filter;
  float f_hx;
  float f_hy;
  VmLubOptF32 f_cx;
  VmLubOptF32 f_cy;
  VmLubOptF32 f_angle;
};
struct VmLub_CircleDesc {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptI32 f_version;
  VmLubOptF32 f_density;
  VmLubOptF32 f_friction;
  VmLubOptF32 f_restitution;
  void *f_tag;
  void *f_material_name;
  VmLubOptI32 f_material_id;
  VmLubOptBool f_sensor;
  VmLubOptBool f_contact;
  VmLubOptBool f_hit;
  VmLubOptBool f_sensor_events;
  VmLubOptBool f_pre_solve;
  VmLub_FilterDesc *f_filter;
  float f_r;
  VmLubOptF32 f_cx;
  VmLubOptF32 f_cy;
};
struct VmLub_CapsuleDesc {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptI32 f_version;
  VmLubOptF32 f_density;
  VmLubOptF32 f_friction;
  VmLubOptF32 f_restitution;
  void *f_tag;
  void *f_material_name;
  VmLubOptI32 f_material_id;
  VmLubOptBool f_sensor;
  VmLubOptBool f_contact;
  VmLubOptBool f_hit;
  VmLubOptBool f_sensor_events;
  VmLubOptBool f_pre_solve;
  VmLub_FilterDesc *f_filter;
  float f_ax;
  float f_ay;
  float f_bx;
  float f_by;
  float f_r;
};
struct VmLub_SegmentDesc {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptI32 f_version;
  VmLubOptF32 f_density;
  VmLubOptF32 f_friction;
  VmLubOptF32 f_restitution;
  void *f_tag;
  void *f_material_name;
  VmLubOptI32 f_material_id;
  VmLubOptBool f_sensor;
  VmLubOptBool f_contact;
  VmLubOptBool f_hit;
  VmLubOptBool f_sensor_events;
  VmLubOptBool f_pre_solve;
  VmLub_FilterDesc *f_filter;
  float f_ax;
  float f_ay;
  float f_bx;
  float f_by;
};
struct VmLub_PolygonDesc {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptI32 f_version;
  VmLubOptF32 f_density;
  VmLubOptF32 f_friction;
  VmLubOptF32 f_restitution;
  void *f_tag;
  void *f_material_name;
  VmLubOptI32 f_material_id;
  VmLubOptBool f_sensor;
  VmLubOptBool f_contact;
  VmLubOptBool f_hit;
  VmLubOptBool f_sensor_events;
  VmLubOptBool f_pre_solve;
  VmLub_FilterDesc *f_filter;
  void *f_points;
  VmLubOptF32 f_radius;
  VmLubOptF32 f_cx;
  VmLubOptF32 f_cy;
  VmLubOptF32 f_angle;
};
struct VmLub_ChainMaterial {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptF32 f_friction;
  VmLubOptF32 f_restitution;
  VmLubOptI32 f_material_id;
};
struct VmLub_ChainDesc {
  uint32_t type_id;
  uint64_t host_value;
  int32_t f_version;
  void *f_points;
  void *f_materials;
  VmLubOptBool f_loop;
  VmLubOptF32 f_friction;
  VmLubOptF32 f_restitution;
  void *f_tag;
  void *f_material_name;
  VmLubOptI32 f_material_id;
  VmLubOptBool f_sensor_events;
  VmLub_FilterDesc *f_filter;
};
struct VmLub_JointSpringDesc {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptBool f_enabled;
  VmLubOptF32 f_hertz;
  VmLubOptF32 f_damping_ratio;
  VmLubOptF32 f_linear_hertz;
  VmLubOptF32 f_linear_damping_ratio;
  VmLubOptF32 f_angular_hertz;
  VmLubOptF32 f_angular_damping_ratio;
};
struct VmLub_JointLimitDesc {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptBool f_enabled;
  VmLubOptF32 f_lower;
  VmLubOptF32 f_upper;
  VmLubOptF32 f_min_length;
  VmLubOptF32 f_max_length;
};
struct VmLub_JointMotorDesc {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptBool f_enabled;
  VmLubOptF32 f_speed;
  VmLubOptF32 f_max_force;
  VmLubOptF32 f_max_torque;
  VmLub_Vec2d *f_linear_offset;
  VmLubOptF32 f_angular_offset;
  VmLubOptF32 f_correction_factor;
};
struct VmLub_JointTargetDesc {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptF32 f_x;
  VmLubOptF32 f_y;
  VmLubOptF32 f_translation;
  VmLubOptF32 f_angle;
  VmLub_Vec2d *f_linear_offset;
  VmLubOptF32 f_angular_offset;
};
struct VmLub_JointDesc {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptI32 f_version;
  VmLubOptI32 f_type;
  VmLub_BodyRef *f_body_a;
  VmLub_BodyRef *f_body_b;
  VmLub_Vec2d *f_anchor_a;
  VmLub_Vec2d *f_anchor_b;
  VmLub_Vec2d *f_local_anchor_a;
  VmLub_Vec2d *f_local_anchor_b;
  VmLub_Vec2d *f_local_axis_a;
  VmLubOptF32 f_reference_angle;
  VmLubOptBool f_collide_connected;
  VmLubOptF32 f_length;
  VmLubOptF32 f_min_length;
  VmLubOptF32 f_max_length;
  VmLubOptF32 f_lower;
  VmLubOptF32 f_upper;
  VmLubOptF32 f_target_angle;
  VmLubOptF32 f_target_translation;
  VmLub_Vec2d *f_linear_offset;
  VmLubOptF32 f_angular_offset;
  VmLubOptF32 f_hertz;
  VmLubOptF32 f_damping_ratio;
  VmLubOptF32 f_max_force;
  VmLubOptF32 f_max_torque;
  VmLubOptF32 f_motor_speed;
  VmLubOptF32 f_correction_factor;
  VmLub_JointSpringDesc *f_spring;
  VmLub_JointLimitDesc *f_limit;
  VmLub_JointMotorDesc *f_motor;
  VmLub_Vec2d *f_target;
};
struct VmLub_CommandOpts {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptBool f_wake;
  VmLub_Vec2d *f_point;
  VmLubOptF32 f_time_step;
};
struct VmLub_VelocityDesc {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptF32 f_vx;
  VmLubOptF32 f_vy;
  VmLubOptF32 f_w;
};
struct VmLub_PoseDesc {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptF32 f_x;
  VmLubOptF32 f_y;
  VmLubOptF32 f_angle;
};
struct VmLub_MassDataDesc {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptF32 f_mass;
  VmLubOptF32 f_inertia;
  VmLub_Vec2d *f_local_center;
};
struct VmLub_MaterialDesc {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptF32 f_density;
  VmLubOptF32 f_friction;
  VmLubOptF32 f_restitution;
  void *f_material_name;
  VmLubOptI32 f_material_id;
};
struct VmLub_ShapeEventsDesc {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptBool f_sensor_events;
  VmLubOptBool f_contact;
  VmLubOptBool f_pre_solve;
  VmLubOptBool f_hit;
};
struct VmLub_RaycastDesc {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptF32 f_x;
  VmLubOptF32 f_y;
  VmLubOptF32 f_dx;
  VmLubOptF32 f_dy;
  VmLubOptF32 f_max_fraction;
  VmLub_FilterDesc *f_filter;
};
struct VmLub_AabbDesc {
  uint32_t type_id;
  uint64_t host_value;
  float f_min_x;
  float f_min_y;
  float f_max_x;
  float f_max_y;
  VmLub_FilterDesc *f_filter;
};
struct VmLub_ShapeCastDesc {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptI32 f_kind;
  VmLubOptF32 f_x;
  VmLubOptF32 f_y;
  VmLubOptF32 f_angle;
  VmLubOptF32 f_radius;
  VmLubOptF32 f_cx;
  VmLubOptF32 f_cy;
  VmLubOptF32 f_ax;
  VmLubOptF32 f_ay;
  VmLubOptF32 f_bx;
  VmLubOptF32 f_by;
  VmLubOptF32 f_hx;
  VmLubOptF32 f_hy;
  void *f_points;
  VmLubOptF32 f_dx;
  VmLubOptF32 f_dy;
  VmLubOptF32 f_max_fraction;
  VmLub_FilterDesc *f_filter;
};
struct VmLub_MoverDesc {
  uint32_t type_id;
  uint64_t host_value;
  float f_ax;
  float f_ay;
  float f_bx;
  float f_by;
  float f_r;
  VmLubOptF32 f_dx;
  VmLubOptF32 f_dy;
  VmLubOptF32 f_max_fraction;
  VmLub_FilterDesc *f_filter;
};
struct VmLub_ExplosionDesc {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptF32 f_x;
  VmLubOptF32 f_y;
  VmLubOptF32 f_radius;
  VmLubOptF32 f_falloff;
  VmLubOptF32 f_impulse_per_length;
  VmLub_FilterDesc *f_filter;
};
struct VmLub_DebugOpts {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptBool f_shapes;
  VmLubOptBool f_joints;
  VmLubOptBool f_joint_extras;
  VmLubOptBool f_bounds;
  VmLubOptBool f_mass;
  VmLubOptBool f_body_names;
  VmLubOptBool f_contacts;
  VmLubOptBool f_graph_colors;
  VmLubOptBool f_contact_normals;
  VmLubOptBool f_contact_impulses;
  VmLubOptBool f_contact_features;
  VmLubOptBool f_friction_impulses;
  VmLubOptBool f_islands;
  VmLub_AabbDesc *f_drawing_bounds;
};
struct VmLub_DebugData {
  uint32_t type_id;
  uint64_t host_value;
  void *f_segments;
  void *f_circles;
  void *f_capsules;
  void *f_polygons;
  void *f_points;
};
struct VmLub_Pose {
  uint32_t type_id;
  uint64_t host_value;
  float f_x;
  float f_y;
  float f_angle;
  float f_vx;
  float f_vy;
  float f_w;
  bool f_awake;
  bool f_enabled;
  bool f_sleep;
  float f_sleep_threshold;
};
struct VmLub_Velocity {
  uint32_t type_id;
  uint64_t host_value;
  float f_x;
  float f_y;
  float f_w;
};
struct VmLub_MassData {
  uint32_t type_id;
  uint64_t host_value;
  float f_mass;
  float f_inertia;
  VmLub_Vec2d *f_center;
  VmLub_Vec2d *f_local_center;
};
struct VmLub_Aabb {
  uint32_t type_id;
  uint64_t host_value;
  float f_min_x;
  float f_min_y;
  float f_max_x;
  float f_max_y;
};
struct VmLub_FilterInfo {
  uint32_t type_id;
  uint64_t host_value;
  void *f_category_bits;
  void *f_mask_bits;
  int32_t f_group;
};
struct VmLub_ShapeInfo {
  uint32_t type_id;
  uint64_t host_value;
  void *f_body;
  void *f_shape;
  void *f_tag;
  void *f_chain;
  VmLubOptBool f_segment;
  void *f_material_name;
  VmLubOptI32 f_material_id;
  VmLubOptI32 f_kind;
  void *f_category_bits;
  void *f_mask_bits;
  VmLubOptI32 f_group;
  bool f_valid;
  float f_density;
  float f_friction;
  float f_restitution;
  bool f_sensor;
  bool f_sensor_events;
  bool f_contact;
  bool f_pre_solve;
  bool f_hit;
  VmLub_FilterInfo *f_filter;
  VmLub_Aabb *f_aabb;
};
struct VmLub_WorldCallbackInfo {
  uint32_t type_id;
  uint64_t host_value;
  bool f_filter;
  bool f_pre_solve;
  bool f_friction;
  bool f_restitution;
};
struct VmLub_WorldInfo {
  uint32_t type_id;
  uint64_t host_value;
  void *f_key;
  bool f_valid;
  int32_t f_version;
  int32_t f_generation;
  bool f_begun;
  bool f_prune;
  float f_fixed_dt;
  int32_t f_substeps;
  int32_t f_max_steps;
  float f_accumulator;
  int32_t f_pending_commands;
  VmLub_WorldCallbackInfo *f_callbacks;
  VmLub_Vec2d *f_gravity;
  VmLubOptBool f_sleep;
  VmLubOptBool f_continuous;
  VmLubOptBool f_warm_starting;
  VmLubOptF32 f_restitution_threshold;
  VmLubOptF32 f_hit_event_threshold;
  VmLubOptF32 f_maximum_linear_speed;
  VmLubOptI32 f_awake_body_count;
};
struct VmLub_StepInfo {
  uint32_t type_id;
  uint64_t host_value;
  int32_t f_steps;
  int32_t f_commands;
  float f_alpha;
  bool f_dropped;
  int32_t f_contact_begins;
  int32_t f_contact_ends;
  int32_t f_contact_hits;
  int32_t f_sensor_begins;
  int32_t f_sensor_ends;
  int32_t f_body_moves;
  int32_t f_body_events;
};
struct VmLub_JointView {
  uint32_t type_id;
  uint64_t host_value;
  void *f_joint;
  int32_t f_type;
  void *f_a;
  void *f_b;
  bool f_valid;
};
struct VmLub_JointInfo {
  uint32_t type_id;
  uint64_t host_value;
  void *f_joint;
  int32_t f_type;
  void *f_a;
  void *f_b;
  bool f_valid;
  bool f_collide_connected;
  VmLub_Vec2d *f_force;
  float f_torque;
  float f_linear_separation;
  float f_angular_separation;
  VmLub_Vec2d *f_local_anchor_a;
  VmLub_Vec2d *f_local_anchor_b;
  VmLub_Vec2d *f_local_axis_a;
  VmLubOptF32 f_reference_angle;
};
struct VmLub_ContactData {
  uint32_t type_id;
  uint64_t host_value;
  VmLub_ShapeView *f_a;
  VmLub_ShapeView *f_b;
  float f_nx;
  float f_ny;
  int32_t f_point_count;
  VmLubOptF32 f_x;
  VmLubOptF32 f_y;
  VmLubOptF32 f_separation;
};
struct VmLub_ContactEvent {
  uint32_t type_id;
  uint64_t host_value;
  VmLub_ShapeView *f_a;
  VmLub_ShapeView *f_b;
  float f_nx;
  float f_ny;
  int32_t f_point_count;
  float f_x;
  float f_y;
  VmLubOptF32 f_approach_speed;
};
struct VmLub_SensorEvent {
  uint32_t type_id;
  uint64_t host_value;
  VmLub_ShapeView *f_sensor;
  VmLub_ShapeView *f_visitor;
};
struct VmLub_BodyEvent {
  uint32_t type_id;
  uint64_t host_value;
  void *f_body;
  bool f_valid;
  float f_x;
  float f_y;
  float f_angle;
  bool f_fell_asleep;
};
struct VmLub_RayHit {
  uint32_t type_id;
  uint64_t host_value;
  void *f_body;
  void *f_shape;
  void *f_tag;
  void *f_chain;
  VmLubOptBool f_segment;
  void *f_material_name;
  VmLubOptI32 f_material_id;
  VmLubOptI32 f_kind;
  void *f_category_bits;
  void *f_mask_bits;
  VmLubOptI32 f_group;
  bool f_valid;
  float f_x;
  float f_y;
  float f_nx;
  float f_ny;
  float f_fraction;
  VmLubOptI32 f_node_visits;
  VmLubOptI32 f_leaf_visits;
};
struct VmLub_ShapeRayHit {
  uint32_t type_id;
  uint64_t host_value;
  float f_x;
  float f_y;
  float f_nx;
  float f_ny;
  float f_fraction;
  int32_t f_iterations;
};
struct VmLub_MoverCast {
  uint32_t type_id;
  uint64_t host_value;
  float f_fraction;
  float f_dx;
  float f_dy;
};
struct VmLub_MoverPlane {
  uint32_t type_id;
  uint64_t host_value;
  void *f_body;
  void *f_shape;
  void *f_tag;
  void *f_chain;
  VmLubOptBool f_segment;
  void *f_material_name;
  VmLubOptI32 f_material_id;
  VmLubOptI32 f_kind;
  void *f_category_bits;
  void *f_mask_bits;
  VmLubOptI32 f_group;
  bool f_valid;
  bool f_hit;
  float f_x;
  float f_y;
  float f_nx;
  float f_ny;
  float f_offset;
};
struct VmLub_Profile {
  uint32_t type_id;
  uint64_t host_value;
  float f_step;
  float f_pairs;
  float f_collide;
  float f_solve;
  float f_merge_islands;
  float f_prepare_stages;
  float f_solve_constraints;
  float f_prepare_constraints;
  float f_integrate_velocities;
  float f_warm_start;
  float f_solve_impulses;
  float f_integrate_positions;
  float f_relax_impulses;
  float f_apply_restitution;
  float f_store_impulses;
  float f_split_islands;
  float f_transforms;
  float f_hit_events;
  float f_refit;
  float f_bullets;
  float f_sleep_islands;
  float f_sensors;
};
struct VmLub_Counters {
  uint32_t type_id;
  uint64_t host_value;
  int32_t f_body_count;
  int32_t f_shape_count;
  int32_t f_contact_count;
  int32_t f_joint_count;
  int32_t f_island_count;
  int32_t f_stack_used;
  int32_t f_static_tree_height;
  int32_t f_tree_height;
  int32_t f_byte_count;
  int32_t f_task_count;
  void *f_color_counts;
};
struct VmLub_Vec3d {
  uint32_t type_id;
  uint64_t host_value;
  float f_x;
  float f_y;
  float f_z;
};
struct VmLub_Quat3d {
  uint32_t type_id;
  uint64_t host_value;
  float f_x;
  float f_y;
  float f_z;
  float f_w;
};
struct VmLub_WorldRef3d {
  uint32_t type_id;
  uint64_t host_value;
};
struct VmLub_BodyRef3d {
  uint32_t type_id;
  uint64_t host_value;
};
struct VmLub_ShapeRef3d {
  uint32_t type_id;
  uint64_t host_value;
};
struct VmLub_JointRef3d {
  uint32_t type_id;
  uint64_t host_value;
};
struct VmLub_InitialState3d {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptF32 f_x;
  VmLubOptF32 f_y;
  VmLubOptF32 f_z;
  VmLub_Quat3d *f_quat;
  VmLub_Vec3d *f_euler;
  VmLubOptF32 f_vx;
  VmLubOptF32 f_vy;
  VmLubOptF32 f_vz;
  VmLubOptF32 f_wx;
  VmLubOptF32 f_wy;
  VmLubOptF32 f_wz;
  VmLubOptBool f_awake;
};
struct VmLub_MotionLocks3d {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptBool f_linear_x;
  VmLubOptBool f_linear_y;
  VmLubOptBool f_linear_z;
  VmLubOptBool f_angular_x;
  VmLubOptBool f_angular_y;
  VmLubOptBool f_angular_z;
};
struct VmLub_ShapeView3d {
  uint32_t type_id;
  uint64_t host_value;
  void *f_body;
  void *f_shape;
  void *f_tag;
  void *f_material_name;
  VmLubOptI32 f_material_id;
  VmLubOptI32 f_kind;
  void *f_category_bits;
  void *f_mask_bits;
  VmLubOptI32 f_group;
  bool f_valid;
};
struct VmLub_PreSolveContact3d {
  uint32_t type_id;
  uint64_t host_value;
  VmLub_ShapeView3d *f_a;
  VmLub_ShapeView3d *f_b;
  float f_x;
  float f_y;
  float f_z;
  float f_nx;
  float f_ny;
  float f_nz;
};
struct VmLub_WorldCallbacks3d {
  uint32_t type_id;
  uint64_t host_value;
  void *f_filter;
  void *f_pre_solve;
  void *f_friction;
  void *f_restitution;
};
struct VmLub_WorldOpts3d {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptI32 f_version;
  VmLub_Vec3d *f_gravity;
  VmLubOptF32 f_fixed_dt;
  VmLubOptI32 f_substeps;
  VmLubOptI32 f_max_steps;
  VmLubOptBool f_sleep;
  VmLubOptBool f_continuous;
  VmLubOptF32 f_hit_event_threshold;
  VmLub_WorldCallbacks3d *f_callbacks;
};
struct VmLub_BeginOpts3d {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptBool f_prune;
};
struct VmLub_BodyDesc3d {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptI32 f_version;
  VmLubOptI32 f_type;
  VmLub_MotionLocks3d *f_motion_locks;
  VmLubOptBool f_bullet;
  VmLubOptBool f_enabled;
  VmLubOptBool f_awake;
  VmLubOptBool f_sleep;
  VmLubOptF32 f_sleep_threshold;
  VmLubOptF32 f_gravity_scale;
  VmLubOptF32 f_linear_damping;
  VmLubOptF32 f_angular_damping;
  VmLub_InitialState3d *f_initial;
};
struct VmLub_FilterDesc3d {
  uint32_t type_id;
  uint64_t host_value;
  void *f_category_bits;
  void *f_mask_bits;
  VmLubOptI32 f_group;
};
struct VmLub_ShapeDesc3d {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptI32 f_version;
  VmLubOptF32 f_density;
  VmLubOptF32 f_friction;
  VmLubOptF32 f_restitution;
  void *f_tag;
  void *f_material_name;
  VmLubOptI32 f_material_id;
  VmLubOptBool f_sensor;
  VmLubOptBool f_contact;
  VmLubOptBool f_hit;
  VmLubOptBool f_sensor_events;
  VmLubOptBool f_pre_solve;
  VmLub_FilterDesc3d *f_filter;
};
struct VmLub_SphereDesc3d {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptI32 f_version;
  VmLubOptF32 f_density;
  VmLubOptF32 f_friction;
  VmLubOptF32 f_restitution;
  void *f_tag;
  void *f_material_name;
  VmLubOptI32 f_material_id;
  VmLubOptBool f_sensor;
  VmLubOptBool f_contact;
  VmLubOptBool f_hit;
  VmLubOptBool f_sensor_events;
  VmLubOptBool f_pre_solve;
  VmLub_FilterDesc3d *f_filter;
  float f_r;
  VmLub_Vec3d *f_offset;
};
struct VmLub_BoxDesc3d {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptI32 f_version;
  VmLubOptF32 f_density;
  VmLubOptF32 f_friction;
  VmLubOptF32 f_restitution;
  void *f_tag;
  void *f_material_name;
  VmLubOptI32 f_material_id;
  VmLubOptBool f_sensor;
  VmLubOptBool f_contact;
  VmLubOptBool f_hit;
  VmLubOptBool f_sensor_events;
  VmLubOptBool f_pre_solve;
  VmLub_FilterDesc3d *f_filter;
  float f_hx;
  float f_hy;
  float f_hz;
  VmLub_Vec3d *f_offset;
  VmLub_Quat3d *f_quat;
};
struct VmLub_CapsuleDesc3d {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptI32 f_version;
  VmLubOptF32 f_density;
  VmLubOptF32 f_friction;
  VmLubOptF32 f_restitution;
  void *f_tag;
  void *f_material_name;
  VmLubOptI32 f_material_id;
  VmLubOptBool f_sensor;
  VmLubOptBool f_contact;
  VmLubOptBool f_hit;
  VmLubOptBool f_sensor_events;
  VmLubOptBool f_pre_solve;
  VmLub_FilterDesc3d *f_filter;
  VmLub_Vec3d *f_a;
  VmLub_Vec3d *f_b;
  float f_r;
};
struct VmLub_CylinderDesc3d {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptI32 f_version;
  VmLubOptF32 f_density;
  VmLubOptF32 f_friction;
  VmLubOptF32 f_restitution;
  void *f_tag;
  void *f_material_name;
  VmLubOptI32 f_material_id;
  VmLubOptBool f_sensor;
  VmLubOptBool f_contact;
  VmLubOptBool f_hit;
  VmLubOptBool f_sensor_events;
  VmLubOptBool f_pre_solve;
  VmLub_FilterDesc3d *f_filter;
  float f_height;
  float f_radius;
  VmLubOptI32 f_sides;
  VmLubOptF32 f_y_offset;
};
struct VmLub_ConeDesc3d {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptI32 f_version;
  VmLubOptF32 f_density;
  VmLubOptF32 f_friction;
  VmLubOptF32 f_restitution;
  void *f_tag;
  void *f_material_name;
  VmLubOptI32 f_material_id;
  VmLubOptBool f_sensor;
  VmLubOptBool f_contact;
  VmLubOptBool f_hit;
  VmLubOptBool f_sensor_events;
  VmLubOptBool f_pre_solve;
  VmLub_FilterDesc3d *f_filter;
  float f_height;
  float f_radius1;
  VmLubOptF32 f_radius2;
  VmLubOptI32 f_slices;
};
struct VmLub_HullDesc3d {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptI32 f_version;
  VmLubOptF32 f_density;
  VmLubOptF32 f_friction;
  VmLubOptF32 f_restitution;
  void *f_tag;
  void *f_material_name;
  VmLubOptI32 f_material_id;
  VmLubOptBool f_sensor;
  VmLubOptBool f_contact;
  VmLubOptBool f_hit;
  VmLubOptBool f_sensor_events;
  VmLubOptBool f_pre_solve;
  VmLub_FilterDesc3d *f_filter;
  void *f_points;
  VmLubOptI32 f_max_vertices;
};
struct VmLub_SurfaceMaterial3d {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptF32 f_friction;
  VmLubOptF32 f_restitution;
  VmLubOptI32 f_material_id;
};
struct VmLub_MeshDesc3d {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptI32 f_version;
  VmLubOptF32 f_density;
  VmLubOptF32 f_friction;
  VmLubOptF32 f_restitution;
  void *f_tag;
  void *f_material_name;
  VmLubOptI32 f_material_id;
  VmLubOptBool f_sensor;
  VmLubOptBool f_contact;
  VmLubOptBool f_hit;
  VmLubOptBool f_sensor_events;
  VmLubOptBool f_pre_solve;
  VmLub_FilterDesc3d *f_filter;
  void *f_positions;
  void *f_indices;
  VmLub_Vec3d *f_scale;
  VmLubOptBool f_weld_vertices;
  VmLubOptF32 f_weld_tolerance;
  VmLubOptBool f_use_median_split;
  VmLubOptBool f_identify_edges;
  void *f_materials;
  void *f_material_indices;
};
struct VmLub_HeightFieldDesc3d {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptI32 f_version;
  VmLubOptF32 f_density;
  VmLubOptF32 f_friction;
  VmLubOptF32 f_restitution;
  void *f_tag;
  void *f_material_name;
  VmLubOptI32 f_material_id;
  VmLubOptBool f_sensor;
  VmLubOptBool f_contact;
  VmLubOptBool f_hit;
  VmLubOptBool f_sensor_events;
  VmLubOptBool f_pre_solve;
  VmLub_FilterDesc3d *f_filter;
  void *f_heights;
  int32_t f_x_count;
  int32_t f_z_count;
  VmLubOptF32 f_cell_width;
  VmLub_Vec3d *f_scale;
  VmLubOptF32 f_min_height;
  VmLubOptF32 f_max_height;
  VmLubOptBool f_clockwise_winding;
};
struct VmLub_CompoundSphere3d {
  uint32_t type_id;
  uint64_t host_value;
  float f_r;
  VmLub_Vec3d *f_center;
};
struct VmLub_CompoundBox3d {
  uint32_t type_id;
  uint64_t host_value;
  float f_hx;
  float f_hy;
  float f_hz;
};
struct VmLub_CompoundCapsule3d {
  uint32_t type_id;
  uint64_t host_value;
  VmLub_Vec3d *f_a;
  VmLub_Vec3d *f_b;
  float f_r;
};
struct VmLub_CompoundChild3d {
  uint32_t type_id;
  uint64_t host_value;
  VmLub_FrameDesc3d *f_pose;
  VmLubOptF32 f_friction;
  VmLubOptF32 f_restitution;
  VmLubOptI32 f_material_id;
  VmLub_CompoundSphere3d *f_sphere;
  VmLub_CompoundBox3d *f_box;
  VmLub_CompoundCapsule3d *f_capsule;
};
struct VmLub_CompoundDesc3d {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptI32 f_version;
  VmLubOptF32 f_density;
  VmLubOptF32 f_friction;
  VmLubOptF32 f_restitution;
  void *f_tag;
  void *f_material_name;
  VmLubOptI32 f_material_id;
  VmLubOptBool f_sensor;
  VmLubOptBool f_contact;
  VmLubOptBool f_hit;
  VmLubOptBool f_sensor_events;
  VmLubOptBool f_pre_solve;
  VmLub_FilterDesc3d *f_filter;
  void *f_children;
};
struct VmLub_CommandOpts3d {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptBool f_wake;
  VmLub_Vec3d *f_point;
};
struct VmLub_VelocityDesc3d {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptF32 f_vx;
  VmLubOptF32 f_vy;
  VmLubOptF32 f_vz;
  VmLubOptF32 f_wx;
  VmLubOptF32 f_wy;
  VmLubOptF32 f_wz;
};
struct VmLub_PoseDesc3d {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptF32 f_x;
  VmLubOptF32 f_y;
  VmLubOptF32 f_z;
  VmLub_Quat3d *f_quat;
  VmLub_Vec3d *f_euler;
};
struct VmLub_TargetDesc3d {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptF32 f_x;
  VmLubOptF32 f_y;
  VmLubOptF32 f_z;
  VmLub_Quat3d *f_quat;
  VmLub_Vec3d *f_euler;
  VmLubOptF32 f_time_step;
  VmLubOptBool f_wake;
};
struct VmLub_FrameDesc3d {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptF32 f_x;
  VmLubOptF32 f_y;
  VmLubOptF32 f_z;
  VmLub_Quat3d *f_quat;
  VmLub_Vec3d *f_euler;
};
struct VmLub_JointSpringDesc3d {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptBool f_enabled;
  VmLubOptF32 f_hertz;
  VmLubOptF32 f_damping_ratio;
  VmLubOptF32 f_linear_hertz;
  VmLubOptF32 f_linear_damping_ratio;
  VmLubOptF32 f_angular_hertz;
  VmLubOptF32 f_angular_damping_ratio;
  VmLubOptF32 f_max_torque;
};
struct VmLub_JointLimitDesc3d {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptBool f_enabled;
  VmLubOptF32 f_lower;
  VmLubOptF32 f_upper;
  VmLubOptF32 f_min_length;
  VmLubOptF32 f_max_length;
  VmLubOptF32 f_cone_angle;
  VmLubOptF32 f_lower_twist_angle;
  VmLubOptF32 f_upper_twist_angle;
};
struct VmLub_JointMotorDesc3d {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptBool f_enabled;
  VmLubOptF32 f_speed;
  VmLubOptF32 f_max_force;
  VmLubOptF32 f_max_torque;
  VmLub_Vec3d *f_velocity;
  VmLub_Vec3d *f_linear_velocity;
  VmLub_Vec3d *f_angular_velocity;
  VmLubOptF32 f_max_velocity_force;
  VmLubOptF32 f_max_velocity_torque;
};
struct VmLub_JointTargetDesc3d {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptF32 f_translation;
  VmLubOptF32 f_angle;
  VmLubOptF32 f_steering_angle;
  VmLub_Quat3d *f_quat;
  VmLub_Vec3d *f_euler;
  VmLub_Vec3d *f_linear_velocity;
  VmLub_Vec3d *f_angular_velocity;
};
struct VmLub_JointDesc3d {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptI32 f_version;
  VmLubOptI32 f_type;
  VmLub_BodyRef3d *f_body_a;
  VmLub_BodyRef3d *f_body_b;
  VmLub_Vec3d *f_anchor_a;
  VmLub_Vec3d *f_anchor_b;
  VmLub_Vec3d *f_axis;
  VmLub_FrameDesc3d *f_frame_a;
  VmLub_FrameDesc3d *f_frame_b;
  VmLubOptBool f_collide_connected;
  VmLubOptF32 f_force_threshold;
  VmLubOptF32 f_torque_threshold;
  VmLubOptF32 f_constraint_hertz;
  VmLubOptF32 f_constraint_damping_ratio;
  VmLubOptF32 f_length;
  VmLubOptF32 f_min_length;
  VmLubOptF32 f_max_length;
  VmLubOptF32 f_lower;
  VmLubOptF32 f_upper;
  VmLubOptF32 f_hertz;
  VmLubOptF32 f_damping_ratio;
  VmLubOptF32 f_linear_hertz;
  VmLubOptF32 f_angular_hertz;
  VmLubOptF32 f_linear_damping_ratio;
  VmLubOptF32 f_angular_damping_ratio;
  VmLubOptF32 f_max_force;
  VmLubOptF32 f_max_torque;
  VmLubOptF32 f_max_velocity_force;
  VmLubOptF32 f_max_velocity_torque;
  VmLubOptF32 f_max_spring_force;
  VmLubOptF32 f_max_spring_torque;
  VmLubOptF32 f_motor_speed;
  VmLubOptF32 f_target_angle;
  VmLubOptF32 f_target_translation;
  VmLub_Quat3d *f_target_rotation;
  VmLub_Vec3d *f_linear_velocity;
  VmLub_Vec3d *f_angular_velocity;
  VmLub_Vec3d *f_motor_velocity;
  VmLubOptBool f_enable_spring;
  VmLubOptBool f_enable_limit;
  VmLubOptBool f_enable_motor;
  VmLubOptF32 f_cone_angle;
  VmLubOptBool f_enable_cone_limit;
  VmLubOptBool f_enable_twist_limit;
  VmLubOptF32 f_lower_twist_angle;
  VmLubOptF32 f_upper_twist_angle;
  VmLub_JointSpringDesc3d *f_spring;
  VmLub_JointLimitDesc3d *f_limit;
  VmLub_JointMotorDesc3d *f_motor;
};
struct VmLub_MaterialDesc3d {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptF32 f_density;
  VmLubOptF32 f_friction;
  VmLubOptF32 f_restitution;
  void *f_material_name;
  VmLubOptI32 f_material_id;
};
struct VmLub_ShapeEventsDesc3d {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptBool f_sensor_events;
  VmLubOptBool f_contact;
  VmLubOptBool f_pre_solve;
  VmLubOptBool f_hit;
};
struct VmLub_MoverDesc3d {
  uint32_t type_id;
  uint64_t host_value;
  VmLub_Vec3d *f_a;
  VmLub_Vec3d *f_b;
  float f_r;
  VmLubOptF32 f_dx;
  VmLubOptF32 f_dy;
  VmLubOptF32 f_dz;
  VmLubOptF32 f_max_fraction;
  VmLub_FilterDesc3d *f_filter;
};
struct VmLub_RaycastDesc3d {
  uint32_t type_id;
  uint64_t host_value;
  VmLubOptF32 f_x;
  VmLubOptF32 f_y;
  VmLubOptF32 f_z;
  VmLubOptF32 f_dx;
  VmLubOptF32 f_dy;
  VmLubOptF32 f_dz;
  VmLubOptF32 f_max_fraction;
  VmLub_FilterDesc3d *f_filter;
};
struct VmLub_AabbDesc3d {
  uint32_t type_id;
  uint64_t host_value;
  float f_min_x;
  float f_min_y;
  float f_min_z;
  float f_max_x;
  float f_max_y;
  float f_max_z;
  VmLub_FilterDesc3d *f_filter;
};
struct VmLub_SphereProxy3d {
  uint32_t type_id;
  uint64_t host_value;
  float f_r;
  VmLub_Vec3d *f_center;
};
struct VmLub_BoxProxy3d {
  uint32_t type_id;
  uint64_t host_value;
  float f_hx;
  float f_hy;
  float f_hz;
  VmLubOptF32 f_radius;
  VmLub_Vec3d *f_center;
  VmLub_Quat3d *f_quat;
};
struct VmLub_CapsuleProxy3d {
  uint32_t type_id;
  uint64_t host_value;
  VmLub_Vec3d *f_a;
  VmLub_Vec3d *f_b;
  float f_r;
};
struct VmLub_ShapeProxyDesc3d {
  uint32_t type_id;
  uint64_t host_value;
  VmLub_SphereProxy3d *f_sphere;
  VmLub_BoxProxy3d *f_box;
  VmLub_CapsuleProxy3d *f_capsule;
  VmLubOptF32 f_dx;
  VmLubOptF32 f_dy;
  VmLubOptF32 f_dz;
  VmLubOptF32 f_max_fraction;
  VmLub_FilterDesc3d *f_filter;
};
struct VmLub_Pose3d {
  uint32_t type_id;
  uint64_t host_value;
  float f_x;
  float f_y;
  float f_z;
  float f_qx;
  float f_qy;
  float f_qz;
  float f_qw;
  float f_vx;
  float f_vy;
  float f_vz;
  float f_wx;
  float f_wy;
  float f_wz;
  bool f_awake;
  bool f_enabled;
  bool f_sleep;
  float f_sleep_threshold;
};
struct VmLub_Velocity3d {
  uint32_t type_id;
  uint64_t host_value;
  float f_x;
  float f_y;
  float f_z;
  float f_wx;
  float f_wy;
  float f_wz;
};
struct VmLub_Inertia3d {
  uint32_t type_id;
  uint64_t host_value;
  float f_xx;
  float f_yy;
  float f_zz;
  float f_xy;
  float f_xz;
  float f_yz;
};
struct VmLub_MassData3d {
  uint32_t type_id;
  uint64_t host_value;
  float f_mass;
  VmLub_Vec3d *f_center;
  VmLub_Vec3d *f_local_center;
  VmLub_Inertia3d *f_inertia;
};
struct VmLub_Aabb3d {
  uint32_t type_id;
  uint64_t host_value;
  float f_min_x;
  float f_min_y;
  float f_min_z;
  float f_max_x;
  float f_max_y;
  float f_max_z;
};
struct VmLub_ShapeInfo3d {
  uint32_t type_id;
  uint64_t host_value;
  void *f_body;
  void *f_shape;
  void *f_tag;
  void *f_material_name;
  VmLubOptI32 f_material_id;
  VmLubOptI32 f_kind;
  void *f_category_bits;
  void *f_mask_bits;
  VmLubOptI32 f_group;
  bool f_valid;
  float f_density;
  float f_friction;
  float f_restitution;
  bool f_sensor;
  bool f_sensor_events;
  bool f_contact;
  bool f_pre_solve;
  bool f_hit;
  VmLub_FilterInfo *f_filter;
  VmLub_Aabb3d *f_aabb;
};
struct VmLub_WorldInfo3d {
  uint32_t type_id;
  uint64_t host_value;
  void *f_key;
  bool f_valid;
  int32_t f_version;
  int32_t f_generation;
  bool f_begun;
  bool f_prune;
  float f_fixed_dt;
  int32_t f_substeps;
  int32_t f_max_steps;
  float f_accumulator;
  int32_t f_pending_commands;
  VmLub_Vec3d *f_gravity;
  VmLubOptBool f_sleep;
  VmLubOptBool f_continuous;
  VmLubOptBool f_warm_starting;
  VmLubOptF32 f_restitution_threshold;
  VmLubOptF32 f_hit_event_threshold;
  VmLubOptF32 f_maximum_linear_speed;
  VmLubOptI32 f_awake_body_count;
};
struct VmLub_StepInfo3d {
  uint32_t type_id;
  uint64_t host_value;
  int32_t f_steps;
  int32_t f_commands;
  float f_alpha;
  bool f_dropped;
  int32_t f_contact_begins;
  int32_t f_contact_ends;
  int32_t f_contact_hits;
  int32_t f_sensor_begins;
  int32_t f_sensor_ends;
  int32_t f_body_moves;
  int32_t f_body_events;
  int32_t f_joint_events;
};
struct VmLub_Frame3d {
  uint32_t type_id;
  uint64_t host_value;
  float f_x;
  float f_y;
  float f_z;
  float f_qx;
  float f_qy;
  float f_qz;
  float f_qw;
};
struct VmLub_JointView3d {
  uint32_t type_id;
  uint64_t host_value;
  void *f_joint;
  int32_t f_type;
  void *f_a;
  void *f_b;
  bool f_valid;
};
struct VmLub_JointInfo3d {
  uint32_t type_id;
  uint64_t host_value;
  void *f_joint;
  int32_t f_type;
  void *f_a;
  void *f_b;
  bool f_valid;
  bool f_collide_connected;
  VmLub_Vec3d *f_force;
  VmLub_Vec3d *f_torque;
  float f_linear_separation;
  float f_angular_separation;
  VmLub_Frame3d *f_local_frame_a;
  VmLub_Frame3d *f_local_frame_b;
};
struct VmLub_ContactData3d {
  uint32_t type_id;
  uint64_t host_value;
  VmLub_ShapeView3d *f_a;
  VmLub_ShapeView3d *f_b;
  float f_nx;
  float f_ny;
  float f_nz;
  int32_t f_manifold_count;
  int32_t f_point_count;
  VmLubOptF32 f_x;
  VmLubOptF32 f_y;
  VmLubOptF32 f_z;
  VmLubOptF32 f_separation;
};
struct VmLub_ContactEvent3d {
  uint32_t type_id;
  uint64_t host_value;
  VmLub_ShapeView3d *f_a;
  VmLub_ShapeView3d *f_b;
  float f_nx;
  float f_ny;
  float f_nz;
  int32_t f_point_count;
  float f_x;
  float f_y;
  float f_z;
  VmLubOptF32 f_approach_speed;
};
struct VmLub_SensorEvent3d {
  uint32_t type_id;
  uint64_t host_value;
  VmLub_ShapeView3d *f_sensor;
  VmLub_ShapeView3d *f_visitor;
};
struct VmLub_BodyEvent3d {
  uint32_t type_id;
  uint64_t host_value;
  void *f_body;
  bool f_valid;
  float f_x;
  float f_y;
  float f_z;
  float f_qx;
  float f_qy;
  float f_qz;
  float f_qw;
  bool f_fell_asleep;
};
struct VmLub_JointEvent3d {
  uint32_t type_id;
  uint64_t host_value;
  void *f_joint;
  int32_t f_type;
  void *f_a;
  void *f_b;
  bool f_valid;
};
struct VmLub_RayHit3d {
  uint32_t type_id;
  uint64_t host_value;
  void *f_body;
  void *f_shape;
  void *f_tag;
  void *f_material_name;
  VmLubOptI32 f_material_id;
  VmLubOptI32 f_kind;
  void *f_category_bits;
  void *f_mask_bits;
  VmLubOptI32 f_group;
  bool f_valid;
  float f_x;
  float f_y;
  float f_z;
  float f_nx;
  float f_ny;
  float f_nz;
  float f_fraction;
  int32_t f_hit_material_id;
  int32_t f_triangle_index;
  int32_t f_child_index;
  VmLubOptI32 f_node_visits;
  VmLubOptI32 f_leaf_visits;
};
struct VmLub_ShapeRayHit3d {
  uint32_t type_id;
  uint64_t host_value;
  float f_x;
  float f_y;
  float f_z;
  float f_nx;
  float f_ny;
  float f_nz;
  float f_fraction;
  int32_t f_iterations;
  int32_t f_triangle_index;
  int32_t f_child_index;
};
struct VmLub_MoverCast3d {
  uint32_t type_id;
  uint64_t host_value;
  float f_fraction;
  float f_dx;
  float f_dy;
  float f_dz;
};
struct VmLub_MoverPlane3d {
  uint32_t type_id;
  uint64_t host_value;
  void *f_body;
  void *f_shape;
  void *f_tag;
  void *f_material_name;
  VmLubOptI32 f_material_id;
  VmLubOptI32 f_kind;
  void *f_category_bits;
  void *f_mask_bits;
  VmLubOptI32 f_group;
  bool f_valid;
  float f_x;
  float f_y;
  float f_z;
  float f_nx;
  float f_ny;
  float f_nz;
  float f_offset;
  int32_t f_plane_count;
};
struct VmLub_Profile3d {
  uint32_t type_id;
  uint64_t host_value;
  float f_step;
  float f_pairs;
  float f_collide;
  float f_solve;
  float f_solver_setup;
  float f_constraints;
  float f_prepare_constraints;
  float f_integrate_velocities;
  float f_warm_start;
  float f_solve_impulses;
  float f_integrate_positions;
  float f_relax_impulses;
  float f_apply_restitution;
  float f_store_impulses;
  float f_split_islands;
  float f_transforms;
  float f_sensor_hits;
  float f_joint_events;
  float f_hit_events;
  float f_refit;
  float f_bullets;
  float f_sleep_islands;
  float f_sensors;
};
struct VmLub_Counters3d {
  uint32_t type_id;
  uint64_t host_value;
  int32_t f_body_count;
  int32_t f_shape_count;
  int32_t f_contact_count;
  int32_t f_joint_count;
  int32_t f_island_count;
  int32_t f_stack_used;
  int32_t f_arena_capacity;
  int32_t f_static_tree_height;
  int32_t f_tree_height;
  int32_t f_sat_call_count;
  int32_t f_sat_cache_hit_count;
  int32_t f_byte_count;
  int32_t f_task_count;
  int32_t f_awake_contact_count;
  int32_t f_recycled_contact_count;
  int32_t f_distance_iterations;
  int32_t f_push_back_iterations;
  int32_t f_root_iterations;
  void *f_color_counts;
  void *f_manifold_counts;
};
struct VmLub_EventData {
  uint32_t type_id;
  uint64_t host_value;
  int32_t f_kind;
  int32_t f_key;
  int32_t f_button;
  float f_x;
  float f_y;
  float f_dx;
  float f_dy;
};
_Static_assert(offsetof(VmLub_Bindings, host_value) == 8,
               "the VM puts host_value 8 bytes into the payload");
/* module が持つ型だけ、VM の型の表の offset と C の struct が同じことを確かめる (読み込みと reload の後) */
static const struct {
  const char *type, *field;
  size_t offset;
} vm_lub_layout[] = {
    {"Lub_Bindings", "blob", offsetof(VmLub_Bindings, f_blob)},
    {"Lub_TextureRef", "version", offsetof(VmLub_TextureRef, f_version)},
    {"Lub_ShaderRef", "version", offsetof(VmLub_ShaderRef, f_version)},
    {"Lub_BufferRef", "version", offsetof(VmLub_BufferRef, f_version)},
    {"Lub_Bytes", "length", offsetof(VmLub_Bytes, f_length)},
    {"Lub_PassOpts", "target", offsetof(VmLub_PassOpts, f_target)},
    {"Lub_PassOpts", "targets", offsetof(VmLub_PassOpts, f_targets)},
    {"Lub_PassOpts", "depth_target", offsetof(VmLub_PassOpts, f_depth_target)},
    {"Lub_PassOpts", "clear_color", offsetof(VmLub_PassOpts, f_clear_color)},
    {"Lub_PassOpts", "clear_colors", offsetof(VmLub_PassOpts, f_clear_colors)},
    {"Lub_PassOpts", "clear_depth", offsetof(VmLub_PassOpts, f_clear_depth)},
    {"Lub_PassOpts", "load", offsetof(VmLub_PassOpts, f_load)},
    {"Lub_DrawOpts", "shader", offsetof(VmLub_DrawOpts, f_shader)},
    {"Lub_DrawOpts", "blend", offsetof(VmLub_DrawOpts, f_blend)},
    {"Lub_DrawOpts", "cull", offsetof(VmLub_DrawOpts, f_cull)},
    {"Lub_DrawOpts", "primitive", offsetof(VmLub_DrawOpts, f_primitive)},
    {"Lub_DrawOpts", "depth", offsetof(VmLub_DrawOpts, f_depth)},
    {"Lub_DrawOpts", "depth_write", offsetof(VmLub_DrawOpts, f_depth_write)},
    {"Lub_DispatchOpts", "shader", offsetof(VmLub_DispatchOpts, f_shader)},
    {"Lub_TextureOpts", "filter", offsetof(VmLub_TextureOpts, f_filter)},
    {"Lub_TextureOpts", "wrap", offsetof(VmLub_TextureOpts, f_wrap)},
    {"Lub_TextureOpts", "target", offsetof(VmLub_TextureOpts, f_target)},
    {"Lub_TextureOpts", "storage", offsetof(VmLub_TextureOpts, f_storage)},
    {"Lub_XrView", "target", offsetof(VmLub_XrView, f_target)},
    {"Lub_XrView", "width", offsetof(VmLub_XrView, f_width)},
    {"Lub_XrView", "height", offsetof(VmLub_XrView, f_height)},
    {"Lub_XrView", "position", offsetof(VmLub_XrView, f_position)},
    {"Lub_XrView", "orientation", offsetof(VmLub_XrView, f_orientation)},
    {"Lub_XrView", "view_projection",
     offsetof(VmLub_XrView, f_view_projection)},
    {"Lub_XrInput", "active", offsetof(VmLub_XrInput, f_active)},
    {"Lub_XrInput", "stick_x", offsetof(VmLub_XrInput, f_stick_x)},
    {"Lub_XrInput", "stick_y", offsetof(VmLub_XrInput, f_stick_y)},
    {"Lub_XrInput", "trigger", offsetof(VmLub_XrInput, f_trigger)},
    {"Lub_XrInput", "grip", offsetof(VmLub_XrInput, f_grip)},
    {"Lub_XrInput", "primary", offsetof(VmLub_XrInput, f_primary)},
    {"Lub_XrInput", "secondary", offsetof(VmLub_XrInput, f_secondary)},
    {"Lub_XrInput", "menu", offsetof(VmLub_XrInput, f_menu)},
    {"Lub_XrInput", "stick_click", offsetof(VmLub_XrInput, f_stick_click)},
    {"Lub_ConfigOpts", "backend", offsetof(VmLub_ConfigOpts, f_backend)},
    {"Lub_ConfigOpts", "width", offsetof(VmLub_ConfigOpts, f_width)},
    {"Lub_ConfigOpts", "height", offsetof(VmLub_ConfigOpts, f_height)},
    {"Lub_ConfigOpts", "resource_sweep_after_frames",
     offsetof(VmLub_ConfigOpts, f_resource_sweep_after_frames)},
    {"Lub_ConfigOpts", "readback_depth",
     offsetof(VmLub_ConfigOpts, f_readback_depth)},
    {"Lub_ConfigOpts", "readback_fixed_latency",
     offsetof(VmLub_ConfigOpts, f_readback_fixed_latency)},
    {"Lub_MeshData", "positions", offsetof(VmLub_MeshData, f_positions)},
    {"Lub_MeshData", "normals", offsetof(VmLub_MeshData, f_normals)},
    {"Lub_MeshData", "indices", offsetof(VmLub_MeshData, f_indices)},
    {"Lub_MeshData", "vert_count", offsetof(VmLub_MeshData, f_vert_count)},
    {"Lub_MeshData", "index_count", offsetof(VmLub_MeshData, f_index_count)},
    {"Lub_MeshData", "uvs", offsetof(VmLub_MeshData, f_uvs)},
    {"Lub_MeshData", "tangents", offsetof(VmLub_MeshData, f_tangents)},
    {"Lub_MeshData", "bounds_min", offsetof(VmLub_MeshData, f_bounds_min)},
    {"Lub_MeshData", "bounds_max", offsetof(VmLub_MeshData, f_bounds_max)},
    {"Lub_MeshData", "cell", offsetof(VmLub_MeshData, f_cell)},
    {"Lub_MeshData", "colors", offsetof(VmLub_MeshData, f_colors)},
    {"Lub_MeshData", "metal_rough", offsetof(VmLub_MeshData, f_metal_rough)},
    {"Lub_MeshData", "joints", offsetof(VmLub_MeshData, f_joints)},
    {"Lub_MeshData", "weights", offsetof(VmLub_MeshData, f_weights)},
    {"Lub_MeshData", "bones", offsetof(VmLub_MeshData, f_bones)},
    {"Lub_SdfBone", "name", offsetof(VmLub_SdfBone, f_name)},
    {"Lub_SdfBone", "x", offsetof(VmLub_SdfBone, f_x)},
    {"Lub_SdfBone", "y", offsetof(VmLub_SdfBone, f_y)},
    {"Lub_SdfBone", "z", offsetof(VmLub_SdfBone, f_z)},
    {"Lub_SdfNodeDesc", "op", offsetof(VmLub_SdfNodeDesc, f_op)},
    {"Lub_SdfNodeDesc", "a", offsetof(VmLub_SdfNodeDesc, f_a)},
    {"Lub_SdfNodeDesc", "b", offsetof(VmLub_SdfNodeDesc, f_b)},
    {"Lub_SdfNodeDesc", "params", offsetof(VmLub_SdfNodeDesc, f_params)},
    {"Lub_SdfNodeDesc", "name", offsetof(VmLub_SdfNodeDesc, f_name)},
    {"Lub_GltfMaterial", "base_color_factor",
     offsetof(VmLub_GltfMaterial, f_base_color_factor)},
    {"Lub_GltfMaterial", "metallic_factor",
     offsetof(VmLub_GltfMaterial, f_metallic_factor)},
    {"Lub_GltfMaterial", "roughness_factor",
     offsetof(VmLub_GltfMaterial, f_roughness_factor)},
    {"Lub_GltfMaterial", "alpha_mode",
     offsetof(VmLub_GltfMaterial, f_alpha_mode)},
    {"Lub_GltfMaterial", "alpha_cutoff",
     offsetof(VmLub_GltfMaterial, f_alpha_cutoff)},
    {"Lub_GltfMaterial", "double_sided",
     offsetof(VmLub_GltfMaterial, f_double_sided)},
    {"Lub_GltfMaterial", "normal_scale",
     offsetof(VmLub_GltfMaterial, f_normal_scale)},
    {"Lub_GltfMaterial", "base_color_path",
     offsetof(VmLub_GltfMaterial, f_base_color_path)},
    {"Lub_GltfMaterial", "metallic_roughness_path",
     offsetof(VmLub_GltfMaterial, f_metallic_roughness_path)},
    {"Lub_GltfMaterial", "normal_path",
     offsetof(VmLub_GltfMaterial, f_normal_path)},
    {"Lub_GltfMaterial", "name", offsetof(VmLub_GltfMaterial, f_name)},
    {"Lub_GltfPrimitive", "positions",
     offsetof(VmLub_GltfPrimitive, f_positions)},
    {"Lub_GltfPrimitive", "normals", offsetof(VmLub_GltfPrimitive, f_normals)},
    {"Lub_GltfPrimitive", "indices", offsetof(VmLub_GltfPrimitive, f_indices)},
    {"Lub_GltfPrimitive", "vert_count",
     offsetof(VmLub_GltfPrimitive, f_vert_count)},
    {"Lub_GltfPrimitive", "index_count",
     offsetof(VmLub_GltfPrimitive, f_index_count)},
    {"Lub_GltfPrimitive", "uvs", offsetof(VmLub_GltfPrimitive, f_uvs)},
    {"Lub_GltfPrimitive", "tangents",
     offsetof(VmLub_GltfPrimitive, f_tangents)},
    {"Lub_GltfPrimitive", "bounds_min",
     offsetof(VmLub_GltfPrimitive, f_bounds_min)},
    {"Lub_GltfPrimitive", "bounds_max",
     offsetof(VmLub_GltfPrimitive, f_bounds_max)},
    {"Lub_GltfPrimitive", "cell", offsetof(VmLub_GltfPrimitive, f_cell)},
    {"Lub_GltfPrimitive", "colors", offsetof(VmLub_GltfPrimitive, f_colors)},
    {"Lub_GltfPrimitive", "metal_rough",
     offsetof(VmLub_GltfPrimitive, f_metal_rough)},
    {"Lub_GltfPrimitive", "joints", offsetof(VmLub_GltfPrimitive, f_joints)},
    {"Lub_GltfPrimitive", "weights", offsetof(VmLub_GltfPrimitive, f_weights)},
    {"Lub_GltfPrimitive", "bones", offsetof(VmLub_GltfPrimitive, f_bones)},
    {"Lub_GltfPrimitive", "material_index",
     offsetof(VmLub_GltfPrimitive, f_material_index)},
    {"Lub_GltfPrimitive", "material",
     offsetof(VmLub_GltfPrimitive, f_material)},
    {"Lub_GltfMesh", "positions", offsetof(VmLub_GltfMesh, f_positions)},
    {"Lub_GltfMesh", "normals", offsetof(VmLub_GltfMesh, f_normals)},
    {"Lub_GltfMesh", "indices", offsetof(VmLub_GltfMesh, f_indices)},
    {"Lub_GltfMesh", "vert_count", offsetof(VmLub_GltfMesh, f_vert_count)},
    {"Lub_GltfMesh", "index_count", offsetof(VmLub_GltfMesh, f_index_count)},
    {"Lub_GltfMesh", "uvs", offsetof(VmLub_GltfMesh, f_uvs)},
    {"Lub_GltfMesh", "tangents", offsetof(VmLub_GltfMesh, f_tangents)},
    {"Lub_GltfMesh", "bounds_min", offsetof(VmLub_GltfMesh, f_bounds_min)},
    {"Lub_GltfMesh", "bounds_max", offsetof(VmLub_GltfMesh, f_bounds_max)},
    {"Lub_GltfMesh", "cell", offsetof(VmLub_GltfMesh, f_cell)},
    {"Lub_GltfMesh", "colors", offsetof(VmLub_GltfMesh, f_colors)},
    {"Lub_GltfMesh", "metal_rough", offsetof(VmLub_GltfMesh, f_metal_rough)},
    {"Lub_GltfMesh", "joints", offsetof(VmLub_GltfMesh, f_joints)},
    {"Lub_GltfMesh", "weights", offsetof(VmLub_GltfMesh, f_weights)},
    {"Lub_GltfMesh", "bones", offsetof(VmLub_GltfMesh, f_bones)},
    {"Lub_GltfMesh", "primitives", offsetof(VmLub_GltfMesh, f_primitives)},
    {"Lub_GltfMesh", "material", offsetof(VmLub_GltfMesh, f_material)},
    {"Lub_GlyphBitmap", "w", offsetof(VmLub_GlyphBitmap, f_w)},
    {"Lub_GlyphBitmap", "h", offsetof(VmLub_GlyphBitmap, f_h)},
    {"Lub_GlyphBitmap", "xoff", offsetof(VmLub_GlyphBitmap, f_xoff)},
    {"Lub_GlyphBitmap", "yoff", offsetof(VmLub_GlyphBitmap, f_yoff)},
    {"Lub_GlyphBitmap", "advance", offsetof(VmLub_GlyphBitmap, f_advance)},
    {"Lub_GlyphBitmap", "bytes", offsetof(VmLub_GlyphBitmap, f_bytes)},
    {"Lub_GlyphMesh", "positions", offsetof(VmLub_GlyphMesh, f_positions)},
    {"Lub_GlyphMesh", "normals", offsetof(VmLub_GlyphMesh, f_normals)},
    {"Lub_GlyphMesh", "indices", offsetof(VmLub_GlyphMesh, f_indices)},
    {"Lub_GlyphMesh", "vert_count", offsetof(VmLub_GlyphMesh, f_vert_count)},
    {"Lub_GlyphMesh", "index_count", offsetof(VmLub_GlyphMesh, f_index_count)},
    {"Lub_GlyphMesh", "uvs", offsetof(VmLub_GlyphMesh, f_uvs)},
    {"Lub_GlyphMesh", "tangents", offsetof(VmLub_GlyphMesh, f_tangents)},
    {"Lub_GlyphMesh", "bounds_min", offsetof(VmLub_GlyphMesh, f_bounds_min)},
    {"Lub_GlyphMesh", "bounds_max", offsetof(VmLub_GlyphMesh, f_bounds_max)},
    {"Lub_GlyphMesh", "cell", offsetof(VmLub_GlyphMesh, f_cell)},
    {"Lub_GlyphMesh", "colors", offsetof(VmLub_GlyphMesh, f_colors)},
    {"Lub_GlyphMesh", "metal_rough", offsetof(VmLub_GlyphMesh, f_metal_rough)},
    {"Lub_GlyphMesh", "joints", offsetof(VmLub_GlyphMesh, f_joints)},
    {"Lub_GlyphMesh", "weights", offsetof(VmLub_GlyphMesh, f_weights)},
    {"Lub_GlyphMesh", "bones", offsetof(VmLub_GlyphMesh, f_bones)},
    {"Lub_GlyphMesh", "advance", offsetof(VmLub_GlyphMesh, f_advance)},
    {"Lub_FontMetrics", "ascent", offsetof(VmLub_FontMetrics, f_ascent)},
    {"Lub_FontMetrics", "descent", offsetof(VmLub_FontMetrics, f_descent)},
    {"Lub_FontMetrics", "line_gap", offsetof(VmLub_FontMetrics, f_line_gap)},
    {"Lub_PlayOpts", "volume", offsetof(VmLub_PlayOpts, f_volume)},
    {"Lub_PlayOpts", "pitch", offsetof(VmLub_PlayOpts, f_pitch)},
    {"Lub_PlayOpts", "pan", offsetof(VmLub_PlayOpts, f_pan)},
    {"Lub_VoiceOpts", "volume", offsetof(VmLub_VoiceOpts, f_volume)},
    {"Lub_VoiceOpts", "pitch", offsetof(VmLub_VoiceOpts, f_pitch)},
    {"Lub_VoiceOpts", "pan", offsetof(VmLub_VoiceOpts, f_pan)},
    {"Lub_VoiceOpts", "loop", offsetof(VmLub_VoiceOpts, f_loop)},
    {"Lub_AudioInfo", "device", offsetof(VmLub_AudioInfo, f_device)},
    {"Lub_AudioInfo", "rate", offsetof(VmLub_AudioInfo, f_rate)},
    {"Lub_AudioInfo", "voices", offsetof(VmLub_AudioInfo, f_voices)},
    {"Lub_AudioInfo", "snds", offsetof(VmLub_AudioInfo, f_snds)},
    {"Lub_Vec2d", "x", offsetof(VmLub_Vec2d, f_x)},
    {"Lub_Vec2d", "y", offsetof(VmLub_Vec2d, f_y)},
    {"Lub_InitialState", "x", offsetof(VmLub_InitialState, f_x)},
    {"Lub_InitialState", "y", offsetof(VmLub_InitialState, f_y)},
    {"Lub_InitialState", "angle", offsetof(VmLub_InitialState, f_angle)},
    {"Lub_InitialState", "vx", offsetof(VmLub_InitialState, f_vx)},
    {"Lub_InitialState", "vy", offsetof(VmLub_InitialState, f_vy)},
    {"Lub_InitialState", "w", offsetof(VmLub_InitialState, f_w)},
    {"Lub_InitialState", "awake", offsetof(VmLub_InitialState, f_awake)},
    {"Lub_ShapeView", "body", offsetof(VmLub_ShapeView, f_body)},
    {"Lub_ShapeView", "shape", offsetof(VmLub_ShapeView, f_shape)},
    {"Lub_ShapeView", "tag", offsetof(VmLub_ShapeView, f_tag)},
    {"Lub_ShapeView", "chain", offsetof(VmLub_ShapeView, f_chain)},
    {"Lub_ShapeView", "segment", offsetof(VmLub_ShapeView, f_segment)},
    {"Lub_ShapeView", "material_name",
     offsetof(VmLub_ShapeView, f_material_name)},
    {"Lub_ShapeView", "material_id", offsetof(VmLub_ShapeView, f_material_id)},
    {"Lub_ShapeView", "kind", offsetof(VmLub_ShapeView, f_kind)},
    {"Lub_ShapeView", "category_bits",
     offsetof(VmLub_ShapeView, f_category_bits)},
    {"Lub_ShapeView", "mask_bits", offsetof(VmLub_ShapeView, f_mask_bits)},
    {"Lub_ShapeView", "group", offsetof(VmLub_ShapeView, f_group)},
    {"Lub_ShapeView", "valid", offsetof(VmLub_ShapeView, f_valid)},
    {"Lub_MaterialView", "friction", offsetof(VmLub_MaterialView, f_friction)},
    {"Lub_MaterialView", "restitution",
     offsetof(VmLub_MaterialView, f_restitution)},
    {"Lub_MaterialView", "material_id",
     offsetof(VmLub_MaterialView, f_material_id)},
    {"Lub_ManifoldPoint", "x", offsetof(VmLub_ManifoldPoint, f_x)},
    {"Lub_ManifoldPoint", "y", offsetof(VmLub_ManifoldPoint, f_y)},
    {"Lub_ManifoldPoint", "anchor_a_x",
     offsetof(VmLub_ManifoldPoint, f_anchor_a_x)},
    {"Lub_ManifoldPoint", "anchor_a_y",
     offsetof(VmLub_ManifoldPoint, f_anchor_a_y)},
    {"Lub_ManifoldPoint", "anchor_b_x",
     offsetof(VmLub_ManifoldPoint, f_anchor_b_x)},
    {"Lub_ManifoldPoint", "anchor_b_y",
     offsetof(VmLub_ManifoldPoint, f_anchor_b_y)},
    {"Lub_ManifoldPoint", "separation",
     offsetof(VmLub_ManifoldPoint, f_separation)},
    {"Lub_ManifoldPoint", "normal_impulse",
     offsetof(VmLub_ManifoldPoint, f_normal_impulse)},
    {"Lub_ManifoldPoint", "tangent_impulse",
     offsetof(VmLub_ManifoldPoint, f_tangent_impulse)},
    {"Lub_ManifoldPoint", "total_normal_impulse",
     offsetof(VmLub_ManifoldPoint, f_total_normal_impulse)},
    {"Lub_ManifoldPoint", "normal_velocity",
     offsetof(VmLub_ManifoldPoint, f_normal_velocity)},
    {"Lub_ManifoldPoint", "id", offsetof(VmLub_ManifoldPoint, f_id)},
    {"Lub_ManifoldPoint", "persisted",
     offsetof(VmLub_ManifoldPoint, f_persisted)},
    {"Lub_PreSolveContact", "a", offsetof(VmLub_PreSolveContact, f_a)},
    {"Lub_PreSolveContact", "b", offsetof(VmLub_PreSolveContact, f_b)},
    {"Lub_PreSolveContact", "nx", offsetof(VmLub_PreSolveContact, f_nx)},
    {"Lub_PreSolveContact", "ny", offsetof(VmLub_PreSolveContact, f_ny)},
    {"Lub_PreSolveContact", "rolling_impulse",
     offsetof(VmLub_PreSolveContact, f_rolling_impulse)},
    {"Lub_PreSolveContact", "point_count",
     offsetof(VmLub_PreSolveContact, f_point_count)},
    {"Lub_PreSolveContact", "points",
     offsetof(VmLub_PreSolveContact, f_points)},
    {"Lub_PreSolveContact", "x", offsetof(VmLub_PreSolveContact, f_x)},
    {"Lub_PreSolveContact", "y", offsetof(VmLub_PreSolveContact, f_y)},
    {"Lub_PreSolveContact", "separation",
     offsetof(VmLub_PreSolveContact, f_separation)},
    {"Lub_PreSolveContact", "normal_velocity",
     offsetof(VmLub_PreSolveContact, f_normal_velocity)},
    {"Lub_WorldCallbacks", "filter", offsetof(VmLub_WorldCallbacks, f_filter)},
    {"Lub_WorldCallbacks", "pre_solve",
     offsetof(VmLub_WorldCallbacks, f_pre_solve)},
    {"Lub_WorldCallbacks", "friction",
     offsetof(VmLub_WorldCallbacks, f_friction)},
    {"Lub_WorldCallbacks", "restitution",
     offsetof(VmLub_WorldCallbacks, f_restitution)},
    {"Lub_WorldOpts", "version", offsetof(VmLub_WorldOpts, f_version)},
    {"Lub_WorldOpts", "gravity", offsetof(VmLub_WorldOpts, f_gravity)},
    {"Lub_WorldOpts", "fixed_dt", offsetof(VmLub_WorldOpts, f_fixed_dt)},
    {"Lub_WorldOpts", "substeps", offsetof(VmLub_WorldOpts, f_substeps)},
    {"Lub_WorldOpts", "max_steps", offsetof(VmLub_WorldOpts, f_max_steps)},
    {"Lub_WorldOpts", "sleep", offsetof(VmLub_WorldOpts, f_sleep)},
    {"Lub_WorldOpts", "continuous", offsetof(VmLub_WorldOpts, f_continuous)},
    {"Lub_WorldOpts", "hit_event_threshold",
     offsetof(VmLub_WorldOpts, f_hit_event_threshold)},
    {"Lub_WorldOpts", "callbacks", offsetof(VmLub_WorldOpts, f_callbacks)},
    {"Lub_BeginOpts", "prune", offsetof(VmLub_BeginOpts, f_prune)},
    {"Lub_BodyDesc", "version", offsetof(VmLub_BodyDesc, f_version)},
    {"Lub_BodyDesc", "type", offsetof(VmLub_BodyDesc, f_type)},
    {"Lub_BodyDesc", "fixed_rotation",
     offsetof(VmLub_BodyDesc, f_fixed_rotation)},
    {"Lub_BodyDesc", "bullet", offsetof(VmLub_BodyDesc, f_bullet)},
    {"Lub_BodyDesc", "enabled", offsetof(VmLub_BodyDesc, f_enabled)},
    {"Lub_BodyDesc", "awake", offsetof(VmLub_BodyDesc, f_awake)},
    {"Lub_BodyDesc", "sleep", offsetof(VmLub_BodyDesc, f_sleep)},
    {"Lub_BodyDesc", "sleep_threshold",
     offsetof(VmLub_BodyDesc, f_sleep_threshold)},
    {"Lub_BodyDesc", "gravity_scale",
     offsetof(VmLub_BodyDesc, f_gravity_scale)},
    {"Lub_BodyDesc", "linear_damping",
     offsetof(VmLub_BodyDesc, f_linear_damping)},
    {"Lub_BodyDesc", "angular_damping",
     offsetof(VmLub_BodyDesc, f_angular_damping)},
    {"Lub_BodyDesc", "initial", offsetof(VmLub_BodyDesc, f_initial)},
    {"Lub_FilterDesc", "category_bits",
     offsetof(VmLub_FilterDesc, f_category_bits)},
    {"Lub_FilterDesc", "mask_bits", offsetof(VmLub_FilterDesc, f_mask_bits)},
    {"Lub_FilterDesc", "group", offsetof(VmLub_FilterDesc, f_group)},
    {"Lub_ShapeDesc", "version", offsetof(VmLub_ShapeDesc, f_version)},
    {"Lub_ShapeDesc", "density", offsetof(VmLub_ShapeDesc, f_density)},
    {"Lub_ShapeDesc", "friction", offsetof(VmLub_ShapeDesc, f_friction)},
    {"Lub_ShapeDesc", "restitution", offsetof(VmLub_ShapeDesc, f_restitution)},
    {"Lub_ShapeDesc", "tag", offsetof(VmLub_ShapeDesc, f_tag)},
    {"Lub_ShapeDesc", "material_name",
     offsetof(VmLub_ShapeDesc, f_material_name)},
    {"Lub_ShapeDesc", "material_id", offsetof(VmLub_ShapeDesc, f_material_id)},
    {"Lub_ShapeDesc", "sensor", offsetof(VmLub_ShapeDesc, f_sensor)},
    {"Lub_ShapeDesc", "contact", offsetof(VmLub_ShapeDesc, f_contact)},
    {"Lub_ShapeDesc", "hit", offsetof(VmLub_ShapeDesc, f_hit)},
    {"Lub_ShapeDesc", "sensor_events",
     offsetof(VmLub_ShapeDesc, f_sensor_events)},
    {"Lub_ShapeDesc", "pre_solve", offsetof(VmLub_ShapeDesc, f_pre_solve)},
    {"Lub_ShapeDesc", "filter", offsetof(VmLub_ShapeDesc, f_filter)},
    {"Lub_BoxDesc", "version", offsetof(VmLub_BoxDesc, f_version)},
    {"Lub_BoxDesc", "density", offsetof(VmLub_BoxDesc, f_density)},
    {"Lub_BoxDesc", "friction", offsetof(VmLub_BoxDesc, f_friction)},
    {"Lub_BoxDesc", "restitution", offsetof(VmLub_BoxDesc, f_restitution)},
    {"Lub_BoxDesc", "tag", offsetof(VmLub_BoxDesc, f_tag)},
    {"Lub_BoxDesc", "material_name", offsetof(VmLub_BoxDesc, f_material_name)},
    {"Lub_BoxDesc", "material_id", offsetof(VmLub_BoxDesc, f_material_id)},
    {"Lub_BoxDesc", "sensor", offsetof(VmLub_BoxDesc, f_sensor)},
    {"Lub_BoxDesc", "contact", offsetof(VmLub_BoxDesc, f_contact)},
    {"Lub_BoxDesc", "hit", offsetof(VmLub_BoxDesc, f_hit)},
    {"Lub_BoxDesc", "sensor_events", offsetof(VmLub_BoxDesc, f_sensor_events)},
    {"Lub_BoxDesc", "pre_solve", offsetof(VmLub_BoxDesc, f_pre_solve)},
    {"Lub_BoxDesc", "filter", offsetof(VmLub_BoxDesc, f_filter)},
    {"Lub_BoxDesc", "hx", offsetof(VmLub_BoxDesc, f_hx)},
    {"Lub_BoxDesc", "hy", offsetof(VmLub_BoxDesc, f_hy)},
    {"Lub_BoxDesc", "cx", offsetof(VmLub_BoxDesc, f_cx)},
    {"Lub_BoxDesc", "cy", offsetof(VmLub_BoxDesc, f_cy)},
    {"Lub_BoxDesc", "angle", offsetof(VmLub_BoxDesc, f_angle)},
    {"Lub_CircleDesc", "version", offsetof(VmLub_CircleDesc, f_version)},
    {"Lub_CircleDesc", "density", offsetof(VmLub_CircleDesc, f_density)},
    {"Lub_CircleDesc", "friction", offsetof(VmLub_CircleDesc, f_friction)},
    {"Lub_CircleDesc", "restitution",
     offsetof(VmLub_CircleDesc, f_restitution)},
    {"Lub_CircleDesc", "tag", offsetof(VmLub_CircleDesc, f_tag)},
    {"Lub_CircleDesc", "material_name",
     offsetof(VmLub_CircleDesc, f_material_name)},
    {"Lub_CircleDesc", "material_id",
     offsetof(VmLub_CircleDesc, f_material_id)},
    {"Lub_CircleDesc", "sensor", offsetof(VmLub_CircleDesc, f_sensor)},
    {"Lub_CircleDesc", "contact", offsetof(VmLub_CircleDesc, f_contact)},
    {"Lub_CircleDesc", "hit", offsetof(VmLub_CircleDesc, f_hit)},
    {"Lub_CircleDesc", "sensor_events",
     offsetof(VmLub_CircleDesc, f_sensor_events)},
    {"Lub_CircleDesc", "pre_solve", offsetof(VmLub_CircleDesc, f_pre_solve)},
    {"Lub_CircleDesc", "filter", offsetof(VmLub_CircleDesc, f_filter)},
    {"Lub_CircleDesc", "r", offsetof(VmLub_CircleDesc, f_r)},
    {"Lub_CircleDesc", "cx", offsetof(VmLub_CircleDesc, f_cx)},
    {"Lub_CircleDesc", "cy", offsetof(VmLub_CircleDesc, f_cy)},
    {"Lub_CapsuleDesc", "version", offsetof(VmLub_CapsuleDesc, f_version)},
    {"Lub_CapsuleDesc", "density", offsetof(VmLub_CapsuleDesc, f_density)},
    {"Lub_CapsuleDesc", "friction", offsetof(VmLub_CapsuleDesc, f_friction)},
    {"Lub_CapsuleDesc", "restitution",
     offsetof(VmLub_CapsuleDesc, f_restitution)},
    {"Lub_CapsuleDesc", "tag", offsetof(VmLub_CapsuleDesc, f_tag)},
    {"Lub_CapsuleDesc", "material_name",
     offsetof(VmLub_CapsuleDesc, f_material_name)},
    {"Lub_CapsuleDesc", "material_id",
     offsetof(VmLub_CapsuleDesc, f_material_id)},
    {"Lub_CapsuleDesc", "sensor", offsetof(VmLub_CapsuleDesc, f_sensor)},
    {"Lub_CapsuleDesc", "contact", offsetof(VmLub_CapsuleDesc, f_contact)},
    {"Lub_CapsuleDesc", "hit", offsetof(VmLub_CapsuleDesc, f_hit)},
    {"Lub_CapsuleDesc", "sensor_events",
     offsetof(VmLub_CapsuleDesc, f_sensor_events)},
    {"Lub_CapsuleDesc", "pre_solve", offsetof(VmLub_CapsuleDesc, f_pre_solve)},
    {"Lub_CapsuleDesc", "filter", offsetof(VmLub_CapsuleDesc, f_filter)},
    {"Lub_CapsuleDesc", "ax", offsetof(VmLub_CapsuleDesc, f_ax)},
    {"Lub_CapsuleDesc", "ay", offsetof(VmLub_CapsuleDesc, f_ay)},
    {"Lub_CapsuleDesc", "bx", offsetof(VmLub_CapsuleDesc, f_bx)},
    {"Lub_CapsuleDesc", "by", offsetof(VmLub_CapsuleDesc, f_by)},
    {"Lub_CapsuleDesc", "r", offsetof(VmLub_CapsuleDesc, f_r)},
    {"Lub_SegmentDesc", "version", offsetof(VmLub_SegmentDesc, f_version)},
    {"Lub_SegmentDesc", "density", offsetof(VmLub_SegmentDesc, f_density)},
    {"Lub_SegmentDesc", "friction", offsetof(VmLub_SegmentDesc, f_friction)},
    {"Lub_SegmentDesc", "restitution",
     offsetof(VmLub_SegmentDesc, f_restitution)},
    {"Lub_SegmentDesc", "tag", offsetof(VmLub_SegmentDesc, f_tag)},
    {"Lub_SegmentDesc", "material_name",
     offsetof(VmLub_SegmentDesc, f_material_name)},
    {"Lub_SegmentDesc", "material_id",
     offsetof(VmLub_SegmentDesc, f_material_id)},
    {"Lub_SegmentDesc", "sensor", offsetof(VmLub_SegmentDesc, f_sensor)},
    {"Lub_SegmentDesc", "contact", offsetof(VmLub_SegmentDesc, f_contact)},
    {"Lub_SegmentDesc", "hit", offsetof(VmLub_SegmentDesc, f_hit)},
    {"Lub_SegmentDesc", "sensor_events",
     offsetof(VmLub_SegmentDesc, f_sensor_events)},
    {"Lub_SegmentDesc", "pre_solve", offsetof(VmLub_SegmentDesc, f_pre_solve)},
    {"Lub_SegmentDesc", "filter", offsetof(VmLub_SegmentDesc, f_filter)},
    {"Lub_SegmentDesc", "ax", offsetof(VmLub_SegmentDesc, f_ax)},
    {"Lub_SegmentDesc", "ay", offsetof(VmLub_SegmentDesc, f_ay)},
    {"Lub_SegmentDesc", "bx", offsetof(VmLub_SegmentDesc, f_bx)},
    {"Lub_SegmentDesc", "by", offsetof(VmLub_SegmentDesc, f_by)},
    {"Lub_PolygonDesc", "version", offsetof(VmLub_PolygonDesc, f_version)},
    {"Lub_PolygonDesc", "density", offsetof(VmLub_PolygonDesc, f_density)},
    {"Lub_PolygonDesc", "friction", offsetof(VmLub_PolygonDesc, f_friction)},
    {"Lub_PolygonDesc", "restitution",
     offsetof(VmLub_PolygonDesc, f_restitution)},
    {"Lub_PolygonDesc", "tag", offsetof(VmLub_PolygonDesc, f_tag)},
    {"Lub_PolygonDesc", "material_name",
     offsetof(VmLub_PolygonDesc, f_material_name)},
    {"Lub_PolygonDesc", "material_id",
     offsetof(VmLub_PolygonDesc, f_material_id)},
    {"Lub_PolygonDesc", "sensor", offsetof(VmLub_PolygonDesc, f_sensor)},
    {"Lub_PolygonDesc", "contact", offsetof(VmLub_PolygonDesc, f_contact)},
    {"Lub_PolygonDesc", "hit", offsetof(VmLub_PolygonDesc, f_hit)},
    {"Lub_PolygonDesc", "sensor_events",
     offsetof(VmLub_PolygonDesc, f_sensor_events)},
    {"Lub_PolygonDesc", "pre_solve", offsetof(VmLub_PolygonDesc, f_pre_solve)},
    {"Lub_PolygonDesc", "filter", offsetof(VmLub_PolygonDesc, f_filter)},
    {"Lub_PolygonDesc", "points", offsetof(VmLub_PolygonDesc, f_points)},
    {"Lub_PolygonDesc", "radius", offsetof(VmLub_PolygonDesc, f_radius)},
    {"Lub_PolygonDesc", "cx", offsetof(VmLub_PolygonDesc, f_cx)},
    {"Lub_PolygonDesc", "cy", offsetof(VmLub_PolygonDesc, f_cy)},
    {"Lub_PolygonDesc", "angle", offsetof(VmLub_PolygonDesc, f_angle)},
    {"Lub_ChainMaterial", "friction",
     offsetof(VmLub_ChainMaterial, f_friction)},
    {"Lub_ChainMaterial", "restitution",
     offsetof(VmLub_ChainMaterial, f_restitution)},
    {"Lub_ChainMaterial", "material_id",
     offsetof(VmLub_ChainMaterial, f_material_id)},
    {"Lub_ChainDesc", "version", offsetof(VmLub_ChainDesc, f_version)},
    {"Lub_ChainDesc", "points", offsetof(VmLub_ChainDesc, f_points)},
    {"Lub_ChainDesc", "materials", offsetof(VmLub_ChainDesc, f_materials)},
    {"Lub_ChainDesc", "loop", offsetof(VmLub_ChainDesc, f_loop)},
    {"Lub_ChainDesc", "friction", offsetof(VmLub_ChainDesc, f_friction)},
    {"Lub_ChainDesc", "restitution", offsetof(VmLub_ChainDesc, f_restitution)},
    {"Lub_ChainDesc", "tag", offsetof(VmLub_ChainDesc, f_tag)},
    {"Lub_ChainDesc", "material_name",
     offsetof(VmLub_ChainDesc, f_material_name)},
    {"Lub_ChainDesc", "material_id", offsetof(VmLub_ChainDesc, f_material_id)},
    {"Lub_ChainDesc", "sensor_events",
     offsetof(VmLub_ChainDesc, f_sensor_events)},
    {"Lub_ChainDesc", "filter", offsetof(VmLub_ChainDesc, f_filter)},
    {"Lub_JointSpringDesc", "enabled",
     offsetof(VmLub_JointSpringDesc, f_enabled)},
    {"Lub_JointSpringDesc", "hertz", offsetof(VmLub_JointSpringDesc, f_hertz)},
    {"Lub_JointSpringDesc", "damping_ratio",
     offsetof(VmLub_JointSpringDesc, f_damping_ratio)},
    {"Lub_JointSpringDesc", "linear_hertz",
     offsetof(VmLub_JointSpringDesc, f_linear_hertz)},
    {"Lub_JointSpringDesc", "linear_damping_ratio",
     offsetof(VmLub_JointSpringDesc, f_linear_damping_ratio)},
    {"Lub_JointSpringDesc", "angular_hertz",
     offsetof(VmLub_JointSpringDesc, f_angular_hertz)},
    {"Lub_JointSpringDesc", "angular_damping_ratio",
     offsetof(VmLub_JointSpringDesc, f_angular_damping_ratio)},
    {"Lub_JointLimitDesc", "enabled",
     offsetof(VmLub_JointLimitDesc, f_enabled)},
    {"Lub_JointLimitDesc", "lower", offsetof(VmLub_JointLimitDesc, f_lower)},
    {"Lub_JointLimitDesc", "upper", offsetof(VmLub_JointLimitDesc, f_upper)},
    {"Lub_JointLimitDesc", "min_length",
     offsetof(VmLub_JointLimitDesc, f_min_length)},
    {"Lub_JointLimitDesc", "max_length",
     offsetof(VmLub_JointLimitDesc, f_max_length)},
    {"Lub_JointMotorDesc", "enabled",
     offsetof(VmLub_JointMotorDesc, f_enabled)},
    {"Lub_JointMotorDesc", "speed", offsetof(VmLub_JointMotorDesc, f_speed)},
    {"Lub_JointMotorDesc", "max_force",
     offsetof(VmLub_JointMotorDesc, f_max_force)},
    {"Lub_JointMotorDesc", "max_torque",
     offsetof(VmLub_JointMotorDesc, f_max_torque)},
    {"Lub_JointMotorDesc", "linear_offset",
     offsetof(VmLub_JointMotorDesc, f_linear_offset)},
    {"Lub_JointMotorDesc", "angular_offset",
     offsetof(VmLub_JointMotorDesc, f_angular_offset)},
    {"Lub_JointMotorDesc", "correction_factor",
     offsetof(VmLub_JointMotorDesc, f_correction_factor)},
    {"Lub_JointTargetDesc", "x", offsetof(VmLub_JointTargetDesc, f_x)},
    {"Lub_JointTargetDesc", "y", offsetof(VmLub_JointTargetDesc, f_y)},
    {"Lub_JointTargetDesc", "translation",
     offsetof(VmLub_JointTargetDesc, f_translation)},
    {"Lub_JointTargetDesc", "angle", offsetof(VmLub_JointTargetDesc, f_angle)},
    {"Lub_JointTargetDesc", "linear_offset",
     offsetof(VmLub_JointTargetDesc, f_linear_offset)},
    {"Lub_JointTargetDesc", "angular_offset",
     offsetof(VmLub_JointTargetDesc, f_angular_offset)},
    {"Lub_JointDesc", "version", offsetof(VmLub_JointDesc, f_version)},
    {"Lub_JointDesc", "type", offsetof(VmLub_JointDesc, f_type)},
    {"Lub_JointDesc", "body_a", offsetof(VmLub_JointDesc, f_body_a)},
    {"Lub_JointDesc", "body_b", offsetof(VmLub_JointDesc, f_body_b)},
    {"Lub_JointDesc", "anchor_a", offsetof(VmLub_JointDesc, f_anchor_a)},
    {"Lub_JointDesc", "anchor_b", offsetof(VmLub_JointDesc, f_anchor_b)},
    {"Lub_JointDesc", "local_anchor_a",
     offsetof(VmLub_JointDesc, f_local_anchor_a)},
    {"Lub_JointDesc", "local_anchor_b",
     offsetof(VmLub_JointDesc, f_local_anchor_b)},
    {"Lub_JointDesc", "local_axis_a",
     offsetof(VmLub_JointDesc, f_local_axis_a)},
    {"Lub_JointDesc", "reference_angle",
     offsetof(VmLub_JointDesc, f_reference_angle)},
    {"Lub_JointDesc", "collide_connected",
     offsetof(VmLub_JointDesc, f_collide_connected)},
    {"Lub_JointDesc", "length", offsetof(VmLub_JointDesc, f_length)},
    {"Lub_JointDesc", "min_length", offsetof(VmLub_JointDesc, f_min_length)},
    {"Lub_JointDesc", "max_length", offsetof(VmLub_JointDesc, f_max_length)},
    {"Lub_JointDesc", "lower", offsetof(VmLub_JointDesc, f_lower)},
    {"Lub_JointDesc", "upper", offsetof(VmLub_JointDesc, f_upper)},
    {"Lub_JointDesc", "target_angle",
     offsetof(VmLub_JointDesc, f_target_angle)},
    {"Lub_JointDesc", "target_translation",
     offsetof(VmLub_JointDesc, f_target_translation)},
    {"Lub_JointDesc", "linear_offset",
     offsetof(VmLub_JointDesc, f_linear_offset)},
    {"Lub_JointDesc", "angular_offset",
     offsetof(VmLub_JointDesc, f_angular_offset)},
    {"Lub_JointDesc", "hertz", offsetof(VmLub_JointDesc, f_hertz)},
    {"Lub_JointDesc", "damping_ratio",
     offsetof(VmLub_JointDesc, f_damping_ratio)},
    {"Lub_JointDesc", "max_force", offsetof(VmLub_JointDesc, f_max_force)},
    {"Lub_JointDesc", "max_torque", offsetof(VmLub_JointDesc, f_max_torque)},
    {"Lub_JointDesc", "motor_speed", offsetof(VmLub_JointDesc, f_motor_speed)},
    {"Lub_JointDesc", "correction_factor",
     offsetof(VmLub_JointDesc, f_correction_factor)},
    {"Lub_JointDesc", "spring", offsetof(VmLub_JointDesc, f_spring)},
    {"Lub_JointDesc", "limit", offsetof(VmLub_JointDesc, f_limit)},
    {"Lub_JointDesc", "motor", offsetof(VmLub_JointDesc, f_motor)},
    {"Lub_JointDesc", "target", offsetof(VmLub_JointDesc, f_target)},
    {"Lub_CommandOpts", "wake", offsetof(VmLub_CommandOpts, f_wake)},
    {"Lub_CommandOpts", "point", offsetof(VmLub_CommandOpts, f_point)},
    {"Lub_CommandOpts", "time_step", offsetof(VmLub_CommandOpts, f_time_step)},
    {"Lub_VelocityDesc", "vx", offsetof(VmLub_VelocityDesc, f_vx)},
    {"Lub_VelocityDesc", "vy", offsetof(VmLub_VelocityDesc, f_vy)},
    {"Lub_VelocityDesc", "w", offsetof(VmLub_VelocityDesc, f_w)},
    {"Lub_PoseDesc", "x", offsetof(VmLub_PoseDesc, f_x)},
    {"Lub_PoseDesc", "y", offsetof(VmLub_PoseDesc, f_y)},
    {"Lub_PoseDesc", "angle", offsetof(VmLub_PoseDesc, f_angle)},
    {"Lub_MassDataDesc", "mass", offsetof(VmLub_MassDataDesc, f_mass)},
    {"Lub_MassDataDesc", "inertia", offsetof(VmLub_MassDataDesc, f_inertia)},
    {"Lub_MassDataDesc", "local_center",
     offsetof(VmLub_MassDataDesc, f_local_center)},
    {"Lub_MaterialDesc", "density", offsetof(VmLub_MaterialDesc, f_density)},
    {"Lub_MaterialDesc", "friction", offsetof(VmLub_MaterialDesc, f_friction)},
    {"Lub_MaterialDesc", "restitution",
     offsetof(VmLub_MaterialDesc, f_restitution)},
    {"Lub_MaterialDesc", "material_name",
     offsetof(VmLub_MaterialDesc, f_material_name)},
    {"Lub_MaterialDesc", "material_id",
     offsetof(VmLub_MaterialDesc, f_material_id)},
    {"Lub_ShapeEventsDesc", "sensor_events",
     offsetof(VmLub_ShapeEventsDesc, f_sensor_events)},
    {"Lub_ShapeEventsDesc", "contact",
     offsetof(VmLub_ShapeEventsDesc, f_contact)},
    {"Lub_ShapeEventsDesc", "pre_solve",
     offsetof(VmLub_ShapeEventsDesc, f_pre_solve)},
    {"Lub_ShapeEventsDesc", "hit", offsetof(VmLub_ShapeEventsDesc, f_hit)},
    {"Lub_RaycastDesc", "x", offsetof(VmLub_RaycastDesc, f_x)},
    {"Lub_RaycastDesc", "y", offsetof(VmLub_RaycastDesc, f_y)},
    {"Lub_RaycastDesc", "dx", offsetof(VmLub_RaycastDesc, f_dx)},
    {"Lub_RaycastDesc", "dy", offsetof(VmLub_RaycastDesc, f_dy)},
    {"Lub_RaycastDesc", "max_fraction",
     offsetof(VmLub_RaycastDesc, f_max_fraction)},
    {"Lub_RaycastDesc", "filter", offsetof(VmLub_RaycastDesc, f_filter)},
    {"Lub_AabbDesc", "min_x", offsetof(VmLub_AabbDesc, f_min_x)},
    {"Lub_AabbDesc", "min_y", offsetof(VmLub_AabbDesc, f_min_y)},
    {"Lub_AabbDesc", "max_x", offsetof(VmLub_AabbDesc, f_max_x)},
    {"Lub_AabbDesc", "max_y", offsetof(VmLub_AabbDesc, f_max_y)},
    {"Lub_AabbDesc", "filter", offsetof(VmLub_AabbDesc, f_filter)},
    {"Lub_ShapeCastDesc", "kind", offsetof(VmLub_ShapeCastDesc, f_kind)},
    {"Lub_ShapeCastDesc", "x", offsetof(VmLub_ShapeCastDesc, f_x)},
    {"Lub_ShapeCastDesc", "y", offsetof(VmLub_ShapeCastDesc, f_y)},
    {"Lub_ShapeCastDesc", "angle", offsetof(VmLub_ShapeCastDesc, f_angle)},
    {"Lub_ShapeCastDesc", "radius", offsetof(VmLub_ShapeCastDesc, f_radius)},
    {"Lub_ShapeCastDesc", "cx", offsetof(VmLub_ShapeCastDesc, f_cx)},
    {"Lub_ShapeCastDesc", "cy", offsetof(VmLub_ShapeCastDesc, f_cy)},
    {"Lub_ShapeCastDesc", "ax", offsetof(VmLub_ShapeCastDesc, f_ax)},
    {"Lub_ShapeCastDesc", "ay", offsetof(VmLub_ShapeCastDesc, f_ay)},
    {"Lub_ShapeCastDesc", "bx", offsetof(VmLub_ShapeCastDesc, f_bx)},
    {"Lub_ShapeCastDesc", "by", offsetof(VmLub_ShapeCastDesc, f_by)},
    {"Lub_ShapeCastDesc", "hx", offsetof(VmLub_ShapeCastDesc, f_hx)},
    {"Lub_ShapeCastDesc", "hy", offsetof(VmLub_ShapeCastDesc, f_hy)},
    {"Lub_ShapeCastDesc", "points", offsetof(VmLub_ShapeCastDesc, f_points)},
    {"Lub_ShapeCastDesc", "dx", offsetof(VmLub_ShapeCastDesc, f_dx)},
    {"Lub_ShapeCastDesc", "dy", offsetof(VmLub_ShapeCastDesc, f_dy)},
    {"Lub_ShapeCastDesc", "max_fraction",
     offsetof(VmLub_ShapeCastDesc, f_max_fraction)},
    {"Lub_ShapeCastDesc", "filter", offsetof(VmLub_ShapeCastDesc, f_filter)},
    {"Lub_MoverDesc", "ax", offsetof(VmLub_MoverDesc, f_ax)},
    {"Lub_MoverDesc", "ay", offsetof(VmLub_MoverDesc, f_ay)},
    {"Lub_MoverDesc", "bx", offsetof(VmLub_MoverDesc, f_bx)},
    {"Lub_MoverDesc", "by", offsetof(VmLub_MoverDesc, f_by)},
    {"Lub_MoverDesc", "r", offsetof(VmLub_MoverDesc, f_r)},
    {"Lub_MoverDesc", "dx", offsetof(VmLub_MoverDesc, f_dx)},
    {"Lub_MoverDesc", "dy", offsetof(VmLub_MoverDesc, f_dy)},
    {"Lub_MoverDesc", "max_fraction",
     offsetof(VmLub_MoverDesc, f_max_fraction)},
    {"Lub_MoverDesc", "filter", offsetof(VmLub_MoverDesc, f_filter)},
    {"Lub_ExplosionDesc", "x", offsetof(VmLub_ExplosionDesc, f_x)},
    {"Lub_ExplosionDesc", "y", offsetof(VmLub_ExplosionDesc, f_y)},
    {"Lub_ExplosionDesc", "radius", offsetof(VmLub_ExplosionDesc, f_radius)},
    {"Lub_ExplosionDesc", "falloff", offsetof(VmLub_ExplosionDesc, f_falloff)},
    {"Lub_ExplosionDesc", "impulse_per_length",
     offsetof(VmLub_ExplosionDesc, f_impulse_per_length)},
    {"Lub_ExplosionDesc", "filter", offsetof(VmLub_ExplosionDesc, f_filter)},
    {"Lub_DebugOpts", "shapes", offsetof(VmLub_DebugOpts, f_shapes)},
    {"Lub_DebugOpts", "joints", offsetof(VmLub_DebugOpts, f_joints)},
    {"Lub_DebugOpts", "joint_extras",
     offsetof(VmLub_DebugOpts, f_joint_extras)},
    {"Lub_DebugOpts", "bounds", offsetof(VmLub_DebugOpts, f_bounds)},
    {"Lub_DebugOpts", "mass", offsetof(VmLub_DebugOpts, f_mass)},
    {"Lub_DebugOpts", "body_names", offsetof(VmLub_DebugOpts, f_body_names)},
    {"Lub_DebugOpts", "contacts", offsetof(VmLub_DebugOpts, f_contacts)},
    {"Lub_DebugOpts", "graph_colors",
     offsetof(VmLub_DebugOpts, f_graph_colors)},
    {"Lub_DebugOpts", "contact_normals",
     offsetof(VmLub_DebugOpts, f_contact_normals)},
    {"Lub_DebugOpts", "contact_impulses",
     offsetof(VmLub_DebugOpts, f_contact_impulses)},
    {"Lub_DebugOpts", "contact_features",
     offsetof(VmLub_DebugOpts, f_contact_features)},
    {"Lub_DebugOpts", "friction_impulses",
     offsetof(VmLub_DebugOpts, f_friction_impulses)},
    {"Lub_DebugOpts", "islands", offsetof(VmLub_DebugOpts, f_islands)},
    {"Lub_DebugOpts", "drawing_bounds",
     offsetof(VmLub_DebugOpts, f_drawing_bounds)},
    {"Lub_DebugData", "segments", offsetof(VmLub_DebugData, f_segments)},
    {"Lub_DebugData", "circles", offsetof(VmLub_DebugData, f_circles)},
    {"Lub_DebugData", "capsules", offsetof(VmLub_DebugData, f_capsules)},
    {"Lub_DebugData", "polygons", offsetof(VmLub_DebugData, f_polygons)},
    {"Lub_DebugData", "points", offsetof(VmLub_DebugData, f_points)},
    {"Lub_Pose", "x", offsetof(VmLub_Pose, f_x)},
    {"Lub_Pose", "y", offsetof(VmLub_Pose, f_y)},
    {"Lub_Pose", "angle", offsetof(VmLub_Pose, f_angle)},
    {"Lub_Pose", "vx", offsetof(VmLub_Pose, f_vx)},
    {"Lub_Pose", "vy", offsetof(VmLub_Pose, f_vy)},
    {"Lub_Pose", "w", offsetof(VmLub_Pose, f_w)},
    {"Lub_Pose", "awake", offsetof(VmLub_Pose, f_awake)},
    {"Lub_Pose", "enabled", offsetof(VmLub_Pose, f_enabled)},
    {"Lub_Pose", "sleep", offsetof(VmLub_Pose, f_sleep)},
    {"Lub_Pose", "sleep_threshold", offsetof(VmLub_Pose, f_sleep_threshold)},
    {"Lub_Velocity", "x", offsetof(VmLub_Velocity, f_x)},
    {"Lub_Velocity", "y", offsetof(VmLub_Velocity, f_y)},
    {"Lub_Velocity", "w", offsetof(VmLub_Velocity, f_w)},
    {"Lub_MassData", "mass", offsetof(VmLub_MassData, f_mass)},
    {"Lub_MassData", "inertia", offsetof(VmLub_MassData, f_inertia)},
    {"Lub_MassData", "center", offsetof(VmLub_MassData, f_center)},
    {"Lub_MassData", "local_center", offsetof(VmLub_MassData, f_local_center)},
    {"Lub_Aabb", "min_x", offsetof(VmLub_Aabb, f_min_x)},
    {"Lub_Aabb", "min_y", offsetof(VmLub_Aabb, f_min_y)},
    {"Lub_Aabb", "max_x", offsetof(VmLub_Aabb, f_max_x)},
    {"Lub_Aabb", "max_y", offsetof(VmLub_Aabb, f_max_y)},
    {"Lub_FilterInfo", "category_bits",
     offsetof(VmLub_FilterInfo, f_category_bits)},
    {"Lub_FilterInfo", "mask_bits", offsetof(VmLub_FilterInfo, f_mask_bits)},
    {"Lub_FilterInfo", "group", offsetof(VmLub_FilterInfo, f_group)},
    {"Lub_ShapeInfo", "body", offsetof(VmLub_ShapeInfo, f_body)},
    {"Lub_ShapeInfo", "shape", offsetof(VmLub_ShapeInfo, f_shape)},
    {"Lub_ShapeInfo", "tag", offsetof(VmLub_ShapeInfo, f_tag)},
    {"Lub_ShapeInfo", "chain", offsetof(VmLub_ShapeInfo, f_chain)},
    {"Lub_ShapeInfo", "segment", offsetof(VmLub_ShapeInfo, f_segment)},
    {"Lub_ShapeInfo", "material_name",
     offsetof(VmLub_ShapeInfo, f_material_name)},
    {"Lub_ShapeInfo", "material_id", offsetof(VmLub_ShapeInfo, f_material_id)},
    {"Lub_ShapeInfo", "kind", offsetof(VmLub_ShapeInfo, f_kind)},
    {"Lub_ShapeInfo", "category_bits",
     offsetof(VmLub_ShapeInfo, f_category_bits)},
    {"Lub_ShapeInfo", "mask_bits", offsetof(VmLub_ShapeInfo, f_mask_bits)},
    {"Lub_ShapeInfo", "group", offsetof(VmLub_ShapeInfo, f_group)},
    {"Lub_ShapeInfo", "valid", offsetof(VmLub_ShapeInfo, f_valid)},
    {"Lub_ShapeInfo", "density", offsetof(VmLub_ShapeInfo, f_density)},
    {"Lub_ShapeInfo", "friction", offsetof(VmLub_ShapeInfo, f_friction)},
    {"Lub_ShapeInfo", "restitution", offsetof(VmLub_ShapeInfo, f_restitution)},
    {"Lub_ShapeInfo", "sensor", offsetof(VmLub_ShapeInfo, f_sensor)},
    {"Lub_ShapeInfo", "sensor_events",
     offsetof(VmLub_ShapeInfo, f_sensor_events)},
    {"Lub_ShapeInfo", "contact", offsetof(VmLub_ShapeInfo, f_contact)},
    {"Lub_ShapeInfo", "pre_solve", offsetof(VmLub_ShapeInfo, f_pre_solve)},
    {"Lub_ShapeInfo", "hit", offsetof(VmLub_ShapeInfo, f_hit)},
    {"Lub_ShapeInfo", "filter", offsetof(VmLub_ShapeInfo, f_filter)},
    {"Lub_ShapeInfo", "aabb", offsetof(VmLub_ShapeInfo, f_aabb)},
    {"Lub_WorldCallbackInfo", "filter",
     offsetof(VmLub_WorldCallbackInfo, f_filter)},
    {"Lub_WorldCallbackInfo", "pre_solve",
     offsetof(VmLub_WorldCallbackInfo, f_pre_solve)},
    {"Lub_WorldCallbackInfo", "friction",
     offsetof(VmLub_WorldCallbackInfo, f_friction)},
    {"Lub_WorldCallbackInfo", "restitution",
     offsetof(VmLub_WorldCallbackInfo, f_restitution)},
    {"Lub_WorldInfo", "key", offsetof(VmLub_WorldInfo, f_key)},
    {"Lub_WorldInfo", "valid", offsetof(VmLub_WorldInfo, f_valid)},
    {"Lub_WorldInfo", "version", offsetof(VmLub_WorldInfo, f_version)},
    {"Lub_WorldInfo", "generation", offsetof(VmLub_WorldInfo, f_generation)},
    {"Lub_WorldInfo", "begun", offsetof(VmLub_WorldInfo, f_begun)},
    {"Lub_WorldInfo", "prune", offsetof(VmLub_WorldInfo, f_prune)},
    {"Lub_WorldInfo", "fixed_dt", offsetof(VmLub_WorldInfo, f_fixed_dt)},
    {"Lub_WorldInfo", "substeps", offsetof(VmLub_WorldInfo, f_substeps)},
    {"Lub_WorldInfo", "max_steps", offsetof(VmLub_WorldInfo, f_max_steps)},
    {"Lub_WorldInfo", "accumulator", offsetof(VmLub_WorldInfo, f_accumulator)},
    {"Lub_WorldInfo", "pending_commands",
     offsetof(VmLub_WorldInfo, f_pending_commands)},
    {"Lub_WorldInfo", "callbacks", offsetof(VmLub_WorldInfo, f_callbacks)},
    {"Lub_WorldInfo", "gravity", offsetof(VmLub_WorldInfo, f_gravity)},
    {"Lub_WorldInfo", "sleep", offsetof(VmLub_WorldInfo, f_sleep)},
    {"Lub_WorldInfo", "continuous", offsetof(VmLub_WorldInfo, f_continuous)},
    {"Lub_WorldInfo", "warm_starting",
     offsetof(VmLub_WorldInfo, f_warm_starting)},
    {"Lub_WorldInfo", "restitution_threshold",
     offsetof(VmLub_WorldInfo, f_restitution_threshold)},
    {"Lub_WorldInfo", "hit_event_threshold",
     offsetof(VmLub_WorldInfo, f_hit_event_threshold)},
    {"Lub_WorldInfo", "maximum_linear_speed",
     offsetof(VmLub_WorldInfo, f_maximum_linear_speed)},
    {"Lub_WorldInfo", "awake_body_count",
     offsetof(VmLub_WorldInfo, f_awake_body_count)},
    {"Lub_StepInfo", "steps", offsetof(VmLub_StepInfo, f_steps)},
    {"Lub_StepInfo", "commands", offsetof(VmLub_StepInfo, f_commands)},
    {"Lub_StepInfo", "alpha", offsetof(VmLub_StepInfo, f_alpha)},
    {"Lub_StepInfo", "dropped", offsetof(VmLub_StepInfo, f_dropped)},
    {"Lub_StepInfo", "contact_begins",
     offsetof(VmLub_StepInfo, f_contact_begins)},
    {"Lub_StepInfo", "contact_ends", offsetof(VmLub_StepInfo, f_contact_ends)},
    {"Lub_StepInfo", "contact_hits", offsetof(VmLub_StepInfo, f_contact_hits)},
    {"Lub_StepInfo", "sensor_begins",
     offsetof(VmLub_StepInfo, f_sensor_begins)},
    {"Lub_StepInfo", "sensor_ends", offsetof(VmLub_StepInfo, f_sensor_ends)},
    {"Lub_StepInfo", "body_moves", offsetof(VmLub_StepInfo, f_body_moves)},
    {"Lub_StepInfo", "body_events", offsetof(VmLub_StepInfo, f_body_events)},
    {"Lub_JointView", "joint", offsetof(VmLub_JointView, f_joint)},
    {"Lub_JointView", "type", offsetof(VmLub_JointView, f_type)},
    {"Lub_JointView", "a", offsetof(VmLub_JointView, f_a)},
    {"Lub_JointView", "b", offsetof(VmLub_JointView, f_b)},
    {"Lub_JointView", "valid", offsetof(VmLub_JointView, f_valid)},
    {"Lub_JointInfo", "joint", offsetof(VmLub_JointInfo, f_joint)},
    {"Lub_JointInfo", "type", offsetof(VmLub_JointInfo, f_type)},
    {"Lub_JointInfo", "a", offsetof(VmLub_JointInfo, f_a)},
    {"Lub_JointInfo", "b", offsetof(VmLub_JointInfo, f_b)},
    {"Lub_JointInfo", "valid", offsetof(VmLub_JointInfo, f_valid)},
    {"Lub_JointInfo", "collide_connected",
     offsetof(VmLub_JointInfo, f_collide_connected)},
    {"Lub_JointInfo", "force", offsetof(VmLub_JointInfo, f_force)},
    {"Lub_JointInfo", "torque", offsetof(VmLub_JointInfo, f_torque)},
    {"Lub_JointInfo", "linear_separation",
     offsetof(VmLub_JointInfo, f_linear_separation)},
    {"Lub_JointInfo", "angular_separation",
     offsetof(VmLub_JointInfo, f_angular_separation)},
    {"Lub_JointInfo", "local_anchor_a",
     offsetof(VmLub_JointInfo, f_local_anchor_a)},
    {"Lub_JointInfo", "local_anchor_b",
     offsetof(VmLub_JointInfo, f_local_anchor_b)},
    {"Lub_JointInfo", "local_axis_a",
     offsetof(VmLub_JointInfo, f_local_axis_a)},
    {"Lub_JointInfo", "reference_angle",
     offsetof(VmLub_JointInfo, f_reference_angle)},
    {"Lub_ContactData", "a", offsetof(VmLub_ContactData, f_a)},
    {"Lub_ContactData", "b", offsetof(VmLub_ContactData, f_b)},
    {"Lub_ContactData", "nx", offsetof(VmLub_ContactData, f_nx)},
    {"Lub_ContactData", "ny", offsetof(VmLub_ContactData, f_ny)},
    {"Lub_ContactData", "point_count",
     offsetof(VmLub_ContactData, f_point_count)},
    {"Lub_ContactData", "x", offsetof(VmLub_ContactData, f_x)},
    {"Lub_ContactData", "y", offsetof(VmLub_ContactData, f_y)},
    {"Lub_ContactData", "separation",
     offsetof(VmLub_ContactData, f_separation)},
    {"Lub_ContactEvent", "a", offsetof(VmLub_ContactEvent, f_a)},
    {"Lub_ContactEvent", "b", offsetof(VmLub_ContactEvent, f_b)},
    {"Lub_ContactEvent", "nx", offsetof(VmLub_ContactEvent, f_nx)},
    {"Lub_ContactEvent", "ny", offsetof(VmLub_ContactEvent, f_ny)},
    {"Lub_ContactEvent", "point_count",
     offsetof(VmLub_ContactEvent, f_point_count)},
    {"Lub_ContactEvent", "x", offsetof(VmLub_ContactEvent, f_x)},
    {"Lub_ContactEvent", "y", offsetof(VmLub_ContactEvent, f_y)},
    {"Lub_ContactEvent", "approach_speed",
     offsetof(VmLub_ContactEvent, f_approach_speed)},
    {"Lub_SensorEvent", "sensor", offsetof(VmLub_SensorEvent, f_sensor)},
    {"Lub_SensorEvent", "visitor", offsetof(VmLub_SensorEvent, f_visitor)},
    {"Lub_BodyEvent", "body", offsetof(VmLub_BodyEvent, f_body)},
    {"Lub_BodyEvent", "valid", offsetof(VmLub_BodyEvent, f_valid)},
    {"Lub_BodyEvent", "x", offsetof(VmLub_BodyEvent, f_x)},
    {"Lub_BodyEvent", "y", offsetof(VmLub_BodyEvent, f_y)},
    {"Lub_BodyEvent", "angle", offsetof(VmLub_BodyEvent, f_angle)},
    {"Lub_BodyEvent", "fell_asleep", offsetof(VmLub_BodyEvent, f_fell_asleep)},
    {"Lub_RayHit", "body", offsetof(VmLub_RayHit, f_body)},
    {"Lub_RayHit", "shape", offsetof(VmLub_RayHit, f_shape)},
    {"Lub_RayHit", "tag", offsetof(VmLub_RayHit, f_tag)},
    {"Lub_RayHit", "chain", offsetof(VmLub_RayHit, f_chain)},
    {"Lub_RayHit", "segment", offsetof(VmLub_RayHit, f_segment)},
    {"Lub_RayHit", "material_name", offsetof(VmLub_RayHit, f_material_name)},
    {"Lub_RayHit", "material_id", offsetof(VmLub_RayHit, f_material_id)},
    {"Lub_RayHit", "kind", offsetof(VmLub_RayHit, f_kind)},
    {"Lub_RayHit", "category_bits", offsetof(VmLub_RayHit, f_category_bits)},
    {"Lub_RayHit", "mask_bits", offsetof(VmLub_RayHit, f_mask_bits)},
    {"Lub_RayHit", "group", offsetof(VmLub_RayHit, f_group)},
    {"Lub_RayHit", "valid", offsetof(VmLub_RayHit, f_valid)},
    {"Lub_RayHit", "x", offsetof(VmLub_RayHit, f_x)},
    {"Lub_RayHit", "y", offsetof(VmLub_RayHit, f_y)},
    {"Lub_RayHit", "nx", offsetof(VmLub_RayHit, f_nx)},
    {"Lub_RayHit", "ny", offsetof(VmLub_RayHit, f_ny)},
    {"Lub_RayHit", "fraction", offsetof(VmLub_RayHit, f_fraction)},
    {"Lub_RayHit", "node_visits", offsetof(VmLub_RayHit, f_node_visits)},
    {"Lub_RayHit", "leaf_visits", offsetof(VmLub_RayHit, f_leaf_visits)},
    {"Lub_ShapeRayHit", "x", offsetof(VmLub_ShapeRayHit, f_x)},
    {"Lub_ShapeRayHit", "y", offsetof(VmLub_ShapeRayHit, f_y)},
    {"Lub_ShapeRayHit", "nx", offsetof(VmLub_ShapeRayHit, f_nx)},
    {"Lub_ShapeRayHit", "ny", offsetof(VmLub_ShapeRayHit, f_ny)},
    {"Lub_ShapeRayHit", "fraction", offsetof(VmLub_ShapeRayHit, f_fraction)},
    {"Lub_ShapeRayHit", "iterations",
     offsetof(VmLub_ShapeRayHit, f_iterations)},
    {"Lub_MoverCast", "fraction", offsetof(VmLub_MoverCast, f_fraction)},
    {"Lub_MoverCast", "dx", offsetof(VmLub_MoverCast, f_dx)},
    {"Lub_MoverCast", "dy", offsetof(VmLub_MoverCast, f_dy)},
    {"Lub_MoverPlane", "body", offsetof(VmLub_MoverPlane, f_body)},
    {"Lub_MoverPlane", "shape", offsetof(VmLub_MoverPlane, f_shape)},
    {"Lub_MoverPlane", "tag", offsetof(VmLub_MoverPlane, f_tag)},
    {"Lub_MoverPlane", "chain", offsetof(VmLub_MoverPlane, f_chain)},
    {"Lub_MoverPlane", "segment", offsetof(VmLub_MoverPlane, f_segment)},
    {"Lub_MoverPlane", "material_name",
     offsetof(VmLub_MoverPlane, f_material_name)},
    {"Lub_MoverPlane", "material_id",
     offsetof(VmLub_MoverPlane, f_material_id)},
    {"Lub_MoverPlane", "kind", offsetof(VmLub_MoverPlane, f_kind)},
    {"Lub_MoverPlane", "category_bits",
     offsetof(VmLub_MoverPlane, f_category_bits)},
    {"Lub_MoverPlane", "mask_bits", offsetof(VmLub_MoverPlane, f_mask_bits)},
    {"Lub_MoverPlane", "group", offsetof(VmLub_MoverPlane, f_group)},
    {"Lub_MoverPlane", "valid", offsetof(VmLub_MoverPlane, f_valid)},
    {"Lub_MoverPlane", "hit", offsetof(VmLub_MoverPlane, f_hit)},
    {"Lub_MoverPlane", "x", offsetof(VmLub_MoverPlane, f_x)},
    {"Lub_MoverPlane", "y", offsetof(VmLub_MoverPlane, f_y)},
    {"Lub_MoverPlane", "nx", offsetof(VmLub_MoverPlane, f_nx)},
    {"Lub_MoverPlane", "ny", offsetof(VmLub_MoverPlane, f_ny)},
    {"Lub_MoverPlane", "offset", offsetof(VmLub_MoverPlane, f_offset)},
    {"Lub_Profile", "step", offsetof(VmLub_Profile, f_step)},
    {"Lub_Profile", "pairs", offsetof(VmLub_Profile, f_pairs)},
    {"Lub_Profile", "collide", offsetof(VmLub_Profile, f_collide)},
    {"Lub_Profile", "solve", offsetof(VmLub_Profile, f_solve)},
    {"Lub_Profile", "merge_islands", offsetof(VmLub_Profile, f_merge_islands)},
    {"Lub_Profile", "prepare_stages",
     offsetof(VmLub_Profile, f_prepare_stages)},
    {"Lub_Profile", "solve_constraints",
     offsetof(VmLub_Profile, f_solve_constraints)},
    {"Lub_Profile", "prepare_constraints",
     offsetof(VmLub_Profile, f_prepare_constraints)},
    {"Lub_Profile", "integrate_velocities",
     offsetof(VmLub_Profile, f_integrate_velocities)},
    {"Lub_Profile", "warm_start", offsetof(VmLub_Profile, f_warm_start)},
    {"Lub_Profile", "solve_impulses",
     offsetof(VmLub_Profile, f_solve_impulses)},
    {"Lub_Profile", "integrate_positions",
     offsetof(VmLub_Profile, f_integrate_positions)},
    {"Lub_Profile", "relax_impulses",
     offsetof(VmLub_Profile, f_relax_impulses)},
    {"Lub_Profile", "apply_restitution",
     offsetof(VmLub_Profile, f_apply_restitution)},
    {"Lub_Profile", "store_impulses",
     offsetof(VmLub_Profile, f_store_impulses)},
    {"Lub_Profile", "split_islands", offsetof(VmLub_Profile, f_split_islands)},
    {"Lub_Profile", "transforms", offsetof(VmLub_Profile, f_transforms)},
    {"Lub_Profile", "hit_events", offsetof(VmLub_Profile, f_hit_events)},
    {"Lub_Profile", "refit", offsetof(VmLub_Profile, f_refit)},
    {"Lub_Profile", "bullets", offsetof(VmLub_Profile, f_bullets)},
    {"Lub_Profile", "sleep_islands", offsetof(VmLub_Profile, f_sleep_islands)},
    {"Lub_Profile", "sensors", offsetof(VmLub_Profile, f_sensors)},
    {"Lub_Counters", "body_count", offsetof(VmLub_Counters, f_body_count)},
    {"Lub_Counters", "shape_count", offsetof(VmLub_Counters, f_shape_count)},
    {"Lub_Counters", "contact_count",
     offsetof(VmLub_Counters, f_contact_count)},
    {"Lub_Counters", "joint_count", offsetof(VmLub_Counters, f_joint_count)},
    {"Lub_Counters", "island_count", offsetof(VmLub_Counters, f_island_count)},
    {"Lub_Counters", "stack_used", offsetof(VmLub_Counters, f_stack_used)},
    {"Lub_Counters", "static_tree_height",
     offsetof(VmLub_Counters, f_static_tree_height)},
    {"Lub_Counters", "tree_height", offsetof(VmLub_Counters, f_tree_height)},
    {"Lub_Counters", "byte_count", offsetof(VmLub_Counters, f_byte_count)},
    {"Lub_Counters", "task_count", offsetof(VmLub_Counters, f_task_count)},
    {"Lub_Counters", "color_counts", offsetof(VmLub_Counters, f_color_counts)},
    {"Lub_Vec3d", "x", offsetof(VmLub_Vec3d, f_x)},
    {"Lub_Vec3d", "y", offsetof(VmLub_Vec3d, f_y)},
    {"Lub_Vec3d", "z", offsetof(VmLub_Vec3d, f_z)},
    {"Lub_Quat3d", "x", offsetof(VmLub_Quat3d, f_x)},
    {"Lub_Quat3d", "y", offsetof(VmLub_Quat3d, f_y)},
    {"Lub_Quat3d", "z", offsetof(VmLub_Quat3d, f_z)},
    {"Lub_Quat3d", "w", offsetof(VmLub_Quat3d, f_w)},
    {"Lub_InitialState3d", "x", offsetof(VmLub_InitialState3d, f_x)},
    {"Lub_InitialState3d", "y", offsetof(VmLub_InitialState3d, f_y)},
    {"Lub_InitialState3d", "z", offsetof(VmLub_InitialState3d, f_z)},
    {"Lub_InitialState3d", "quat", offsetof(VmLub_InitialState3d, f_quat)},
    {"Lub_InitialState3d", "euler", offsetof(VmLub_InitialState3d, f_euler)},
    {"Lub_InitialState3d", "vx", offsetof(VmLub_InitialState3d, f_vx)},
    {"Lub_InitialState3d", "vy", offsetof(VmLub_InitialState3d, f_vy)},
    {"Lub_InitialState3d", "vz", offsetof(VmLub_InitialState3d, f_vz)},
    {"Lub_InitialState3d", "wx", offsetof(VmLub_InitialState3d, f_wx)},
    {"Lub_InitialState3d", "wy", offsetof(VmLub_InitialState3d, f_wy)},
    {"Lub_InitialState3d", "wz", offsetof(VmLub_InitialState3d, f_wz)},
    {"Lub_InitialState3d", "awake", offsetof(VmLub_InitialState3d, f_awake)},
    {"Lub_MotionLocks3d", "linear_x",
     offsetof(VmLub_MotionLocks3d, f_linear_x)},
    {"Lub_MotionLocks3d", "linear_y",
     offsetof(VmLub_MotionLocks3d, f_linear_y)},
    {"Lub_MotionLocks3d", "linear_z",
     offsetof(VmLub_MotionLocks3d, f_linear_z)},
    {"Lub_MotionLocks3d", "angular_x",
     offsetof(VmLub_MotionLocks3d, f_angular_x)},
    {"Lub_MotionLocks3d", "angular_y",
     offsetof(VmLub_MotionLocks3d, f_angular_y)},
    {"Lub_MotionLocks3d", "angular_z",
     offsetof(VmLub_MotionLocks3d, f_angular_z)},
    {"Lub_ShapeView3d", "body", offsetof(VmLub_ShapeView3d, f_body)},
    {"Lub_ShapeView3d", "shape", offsetof(VmLub_ShapeView3d, f_shape)},
    {"Lub_ShapeView3d", "tag", offsetof(VmLub_ShapeView3d, f_tag)},
    {"Lub_ShapeView3d", "material_name",
     offsetof(VmLub_ShapeView3d, f_material_name)},
    {"Lub_ShapeView3d", "material_id",
     offsetof(VmLub_ShapeView3d, f_material_id)},
    {"Lub_ShapeView3d", "kind", offsetof(VmLub_ShapeView3d, f_kind)},
    {"Lub_ShapeView3d", "category_bits",
     offsetof(VmLub_ShapeView3d, f_category_bits)},
    {"Lub_ShapeView3d", "mask_bits", offsetof(VmLub_ShapeView3d, f_mask_bits)},
    {"Lub_ShapeView3d", "group", offsetof(VmLub_ShapeView3d, f_group)},
    {"Lub_ShapeView3d", "valid", offsetof(VmLub_ShapeView3d, f_valid)},
    {"Lub_PreSolveContact3d", "a", offsetof(VmLub_PreSolveContact3d, f_a)},
    {"Lub_PreSolveContact3d", "b", offsetof(VmLub_PreSolveContact3d, f_b)},
    {"Lub_PreSolveContact3d", "x", offsetof(VmLub_PreSolveContact3d, f_x)},
    {"Lub_PreSolveContact3d", "y", offsetof(VmLub_PreSolveContact3d, f_y)},
    {"Lub_PreSolveContact3d", "z", offsetof(VmLub_PreSolveContact3d, f_z)},
    {"Lub_PreSolveContact3d", "nx", offsetof(VmLub_PreSolveContact3d, f_nx)},
    {"Lub_PreSolveContact3d", "ny", offsetof(VmLub_PreSolveContact3d, f_ny)},
    {"Lub_PreSolveContact3d", "nz", offsetof(VmLub_PreSolveContact3d, f_nz)},
    {"Lub_WorldCallbacks3d", "filter",
     offsetof(VmLub_WorldCallbacks3d, f_filter)},
    {"Lub_WorldCallbacks3d", "pre_solve",
     offsetof(VmLub_WorldCallbacks3d, f_pre_solve)},
    {"Lub_WorldCallbacks3d", "friction",
     offsetof(VmLub_WorldCallbacks3d, f_friction)},
    {"Lub_WorldCallbacks3d", "restitution",
     offsetof(VmLub_WorldCallbacks3d, f_restitution)},
    {"Lub_WorldOpts3d", "version", offsetof(VmLub_WorldOpts3d, f_version)},
    {"Lub_WorldOpts3d", "gravity", offsetof(VmLub_WorldOpts3d, f_gravity)},
    {"Lub_WorldOpts3d", "fixed_dt", offsetof(VmLub_WorldOpts3d, f_fixed_dt)},
    {"Lub_WorldOpts3d", "substeps", offsetof(VmLub_WorldOpts3d, f_substeps)},
    {"Lub_WorldOpts3d", "max_steps", offsetof(VmLub_WorldOpts3d, f_max_steps)},
    {"Lub_WorldOpts3d", "sleep", offsetof(VmLub_WorldOpts3d, f_sleep)},
    {"Lub_WorldOpts3d", "continuous",
     offsetof(VmLub_WorldOpts3d, f_continuous)},
    {"Lub_WorldOpts3d", "hit_event_threshold",
     offsetof(VmLub_WorldOpts3d, f_hit_event_threshold)},
    {"Lub_WorldOpts3d", "callbacks", offsetof(VmLub_WorldOpts3d, f_callbacks)},
    {"Lub_BeginOpts3d", "prune", offsetof(VmLub_BeginOpts3d, f_prune)},
    {"Lub_BodyDesc3d", "version", offsetof(VmLub_BodyDesc3d, f_version)},
    {"Lub_BodyDesc3d", "type", offsetof(VmLub_BodyDesc3d, f_type)},
    {"Lub_BodyDesc3d", "motion_locks",
     offsetof(VmLub_BodyDesc3d, f_motion_locks)},
    {"Lub_BodyDesc3d", "bullet", offsetof(VmLub_BodyDesc3d, f_bullet)},
    {"Lub_BodyDesc3d", "enabled", offsetof(VmLub_BodyDesc3d, f_enabled)},
    {"Lub_BodyDesc3d", "awake", offsetof(VmLub_BodyDesc3d, f_awake)},
    {"Lub_BodyDesc3d", "sleep", offsetof(VmLub_BodyDesc3d, f_sleep)},
    {"Lub_BodyDesc3d", "sleep_threshold",
     offsetof(VmLub_BodyDesc3d, f_sleep_threshold)},
    {"Lub_BodyDesc3d", "gravity_scale",
     offsetof(VmLub_BodyDesc3d, f_gravity_scale)},
    {"Lub_BodyDesc3d", "linear_damping",
     offsetof(VmLub_BodyDesc3d, f_linear_damping)},
    {"Lub_BodyDesc3d", "angular_damping",
     offsetof(VmLub_BodyDesc3d, f_angular_damping)},
    {"Lub_BodyDesc3d", "initial", offsetof(VmLub_BodyDesc3d, f_initial)},
    {"Lub_FilterDesc3d", "category_bits",
     offsetof(VmLub_FilterDesc3d, f_category_bits)},
    {"Lub_FilterDesc3d", "mask_bits",
     offsetof(VmLub_FilterDesc3d, f_mask_bits)},
    {"Lub_FilterDesc3d", "group", offsetof(VmLub_FilterDesc3d, f_group)},
    {"Lub_ShapeDesc3d", "version", offsetof(VmLub_ShapeDesc3d, f_version)},
    {"Lub_ShapeDesc3d", "density", offsetof(VmLub_ShapeDesc3d, f_density)},
    {"Lub_ShapeDesc3d", "friction", offsetof(VmLub_ShapeDesc3d, f_friction)},
    {"Lub_ShapeDesc3d", "restitution",
     offsetof(VmLub_ShapeDesc3d, f_restitution)},
    {"Lub_ShapeDesc3d", "tag", offsetof(VmLub_ShapeDesc3d, f_tag)},
    {"Lub_ShapeDesc3d", "material_name",
     offsetof(VmLub_ShapeDesc3d, f_material_name)},
    {"Lub_ShapeDesc3d", "material_id",
     offsetof(VmLub_ShapeDesc3d, f_material_id)},
    {"Lub_ShapeDesc3d", "sensor", offsetof(VmLub_ShapeDesc3d, f_sensor)},
    {"Lub_ShapeDesc3d", "contact", offsetof(VmLub_ShapeDesc3d, f_contact)},
    {"Lub_ShapeDesc3d", "hit", offsetof(VmLub_ShapeDesc3d, f_hit)},
    {"Lub_ShapeDesc3d", "sensor_events",
     offsetof(VmLub_ShapeDesc3d, f_sensor_events)},
    {"Lub_ShapeDesc3d", "pre_solve", offsetof(VmLub_ShapeDesc3d, f_pre_solve)},
    {"Lub_ShapeDesc3d", "filter", offsetof(VmLub_ShapeDesc3d, f_filter)},
    {"Lub_SphereDesc3d", "version", offsetof(VmLub_SphereDesc3d, f_version)},
    {"Lub_SphereDesc3d", "density", offsetof(VmLub_SphereDesc3d, f_density)},
    {"Lub_SphereDesc3d", "friction", offsetof(VmLub_SphereDesc3d, f_friction)},
    {"Lub_SphereDesc3d", "restitution",
     offsetof(VmLub_SphereDesc3d, f_restitution)},
    {"Lub_SphereDesc3d", "tag", offsetof(VmLub_SphereDesc3d, f_tag)},
    {"Lub_SphereDesc3d", "material_name",
     offsetof(VmLub_SphereDesc3d, f_material_name)},
    {"Lub_SphereDesc3d", "material_id",
     offsetof(VmLub_SphereDesc3d, f_material_id)},
    {"Lub_SphereDesc3d", "sensor", offsetof(VmLub_SphereDesc3d, f_sensor)},
    {"Lub_SphereDesc3d", "contact", offsetof(VmLub_SphereDesc3d, f_contact)},
    {"Lub_SphereDesc3d", "hit", offsetof(VmLub_SphereDesc3d, f_hit)},
    {"Lub_SphereDesc3d", "sensor_events",
     offsetof(VmLub_SphereDesc3d, f_sensor_events)},
    {"Lub_SphereDesc3d", "pre_solve",
     offsetof(VmLub_SphereDesc3d, f_pre_solve)},
    {"Lub_SphereDesc3d", "filter", offsetof(VmLub_SphereDesc3d, f_filter)},
    {"Lub_SphereDesc3d", "r", offsetof(VmLub_SphereDesc3d, f_r)},
    {"Lub_SphereDesc3d", "offset", offsetof(VmLub_SphereDesc3d, f_offset)},
    {"Lub_BoxDesc3d", "version", offsetof(VmLub_BoxDesc3d, f_version)},
    {"Lub_BoxDesc3d", "density", offsetof(VmLub_BoxDesc3d, f_density)},
    {"Lub_BoxDesc3d", "friction", offsetof(VmLub_BoxDesc3d, f_friction)},
    {"Lub_BoxDesc3d", "restitution", offsetof(VmLub_BoxDesc3d, f_restitution)},
    {"Lub_BoxDesc3d", "tag", offsetof(VmLub_BoxDesc3d, f_tag)},
    {"Lub_BoxDesc3d", "material_name",
     offsetof(VmLub_BoxDesc3d, f_material_name)},
    {"Lub_BoxDesc3d", "material_id", offsetof(VmLub_BoxDesc3d, f_material_id)},
    {"Lub_BoxDesc3d", "sensor", offsetof(VmLub_BoxDesc3d, f_sensor)},
    {"Lub_BoxDesc3d", "contact", offsetof(VmLub_BoxDesc3d, f_contact)},
    {"Lub_BoxDesc3d", "hit", offsetof(VmLub_BoxDesc3d, f_hit)},
    {"Lub_BoxDesc3d", "sensor_events",
     offsetof(VmLub_BoxDesc3d, f_sensor_events)},
    {"Lub_BoxDesc3d", "pre_solve", offsetof(VmLub_BoxDesc3d, f_pre_solve)},
    {"Lub_BoxDesc3d", "filter", offsetof(VmLub_BoxDesc3d, f_filter)},
    {"Lub_BoxDesc3d", "hx", offsetof(VmLub_BoxDesc3d, f_hx)},
    {"Lub_BoxDesc3d", "hy", offsetof(VmLub_BoxDesc3d, f_hy)},
    {"Lub_BoxDesc3d", "hz", offsetof(VmLub_BoxDesc3d, f_hz)},
    {"Lub_BoxDesc3d", "offset", offsetof(VmLub_BoxDesc3d, f_offset)},
    {"Lub_BoxDesc3d", "quat", offsetof(VmLub_BoxDesc3d, f_quat)},
    {"Lub_CapsuleDesc3d", "version", offsetof(VmLub_CapsuleDesc3d, f_version)},
    {"Lub_CapsuleDesc3d", "density", offsetof(VmLub_CapsuleDesc3d, f_density)},
    {"Lub_CapsuleDesc3d", "friction",
     offsetof(VmLub_CapsuleDesc3d, f_friction)},
    {"Lub_CapsuleDesc3d", "restitution",
     offsetof(VmLub_CapsuleDesc3d, f_restitution)},
    {"Lub_CapsuleDesc3d", "tag", offsetof(VmLub_CapsuleDesc3d, f_tag)},
    {"Lub_CapsuleDesc3d", "material_name",
     offsetof(VmLub_CapsuleDesc3d, f_material_name)},
    {"Lub_CapsuleDesc3d", "material_id",
     offsetof(VmLub_CapsuleDesc3d, f_material_id)},
    {"Lub_CapsuleDesc3d", "sensor", offsetof(VmLub_CapsuleDesc3d, f_sensor)},
    {"Lub_CapsuleDesc3d", "contact", offsetof(VmLub_CapsuleDesc3d, f_contact)},
    {"Lub_CapsuleDesc3d", "hit", offsetof(VmLub_CapsuleDesc3d, f_hit)},
    {"Lub_CapsuleDesc3d", "sensor_events",
     offsetof(VmLub_CapsuleDesc3d, f_sensor_events)},
    {"Lub_CapsuleDesc3d", "pre_solve",
     offsetof(VmLub_CapsuleDesc3d, f_pre_solve)},
    {"Lub_CapsuleDesc3d", "filter", offsetof(VmLub_CapsuleDesc3d, f_filter)},
    {"Lub_CapsuleDesc3d", "a", offsetof(VmLub_CapsuleDesc3d, f_a)},
    {"Lub_CapsuleDesc3d", "b", offsetof(VmLub_CapsuleDesc3d, f_b)},
    {"Lub_CapsuleDesc3d", "r", offsetof(VmLub_CapsuleDesc3d, f_r)},
    {"Lub_CylinderDesc3d", "version",
     offsetof(VmLub_CylinderDesc3d, f_version)},
    {"Lub_CylinderDesc3d", "density",
     offsetof(VmLub_CylinderDesc3d, f_density)},
    {"Lub_CylinderDesc3d", "friction",
     offsetof(VmLub_CylinderDesc3d, f_friction)},
    {"Lub_CylinderDesc3d", "restitution",
     offsetof(VmLub_CylinderDesc3d, f_restitution)},
    {"Lub_CylinderDesc3d", "tag", offsetof(VmLub_CylinderDesc3d, f_tag)},
    {"Lub_CylinderDesc3d", "material_name",
     offsetof(VmLub_CylinderDesc3d, f_material_name)},
    {"Lub_CylinderDesc3d", "material_id",
     offsetof(VmLub_CylinderDesc3d, f_material_id)},
    {"Lub_CylinderDesc3d", "sensor", offsetof(VmLub_CylinderDesc3d, f_sensor)},
    {"Lub_CylinderDesc3d", "contact",
     offsetof(VmLub_CylinderDesc3d, f_contact)},
    {"Lub_CylinderDesc3d", "hit", offsetof(VmLub_CylinderDesc3d, f_hit)},
    {"Lub_CylinderDesc3d", "sensor_events",
     offsetof(VmLub_CylinderDesc3d, f_sensor_events)},
    {"Lub_CylinderDesc3d", "pre_solve",
     offsetof(VmLub_CylinderDesc3d, f_pre_solve)},
    {"Lub_CylinderDesc3d", "filter", offsetof(VmLub_CylinderDesc3d, f_filter)},
    {"Lub_CylinderDesc3d", "height", offsetof(VmLub_CylinderDesc3d, f_height)},
    {"Lub_CylinderDesc3d", "radius", offsetof(VmLub_CylinderDesc3d, f_radius)},
    {"Lub_CylinderDesc3d", "sides", offsetof(VmLub_CylinderDesc3d, f_sides)},
    {"Lub_CylinderDesc3d", "y_offset",
     offsetof(VmLub_CylinderDesc3d, f_y_offset)},
    {"Lub_ConeDesc3d", "version", offsetof(VmLub_ConeDesc3d, f_version)},
    {"Lub_ConeDesc3d", "density", offsetof(VmLub_ConeDesc3d, f_density)},
    {"Lub_ConeDesc3d", "friction", offsetof(VmLub_ConeDesc3d, f_friction)},
    {"Lub_ConeDesc3d", "restitution",
     offsetof(VmLub_ConeDesc3d, f_restitution)},
    {"Lub_ConeDesc3d", "tag", offsetof(VmLub_ConeDesc3d, f_tag)},
    {"Lub_ConeDesc3d", "material_name",
     offsetof(VmLub_ConeDesc3d, f_material_name)},
    {"Lub_ConeDesc3d", "material_id",
     offsetof(VmLub_ConeDesc3d, f_material_id)},
    {"Lub_ConeDesc3d", "sensor", offsetof(VmLub_ConeDesc3d, f_sensor)},
    {"Lub_ConeDesc3d", "contact", offsetof(VmLub_ConeDesc3d, f_contact)},
    {"Lub_ConeDesc3d", "hit", offsetof(VmLub_ConeDesc3d, f_hit)},
    {"Lub_ConeDesc3d", "sensor_events",
     offsetof(VmLub_ConeDesc3d, f_sensor_events)},
    {"Lub_ConeDesc3d", "pre_solve", offsetof(VmLub_ConeDesc3d, f_pre_solve)},
    {"Lub_ConeDesc3d", "filter", offsetof(VmLub_ConeDesc3d, f_filter)},
    {"Lub_ConeDesc3d", "height", offsetof(VmLub_ConeDesc3d, f_height)},
    {"Lub_ConeDesc3d", "radius1", offsetof(VmLub_ConeDesc3d, f_radius1)},
    {"Lub_ConeDesc3d", "radius2", offsetof(VmLub_ConeDesc3d, f_radius2)},
    {"Lub_ConeDesc3d", "slices", offsetof(VmLub_ConeDesc3d, f_slices)},
    {"Lub_HullDesc3d", "version", offsetof(VmLub_HullDesc3d, f_version)},
    {"Lub_HullDesc3d", "density", offsetof(VmLub_HullDesc3d, f_density)},
    {"Lub_HullDesc3d", "friction", offsetof(VmLub_HullDesc3d, f_friction)},
    {"Lub_HullDesc3d", "restitution",
     offsetof(VmLub_HullDesc3d, f_restitution)},
    {"Lub_HullDesc3d", "tag", offsetof(VmLub_HullDesc3d, f_tag)},
    {"Lub_HullDesc3d", "material_name",
     offsetof(VmLub_HullDesc3d, f_material_name)},
    {"Lub_HullDesc3d", "material_id",
     offsetof(VmLub_HullDesc3d, f_material_id)},
    {"Lub_HullDesc3d", "sensor", offsetof(VmLub_HullDesc3d, f_sensor)},
    {"Lub_HullDesc3d", "contact", offsetof(VmLub_HullDesc3d, f_contact)},
    {"Lub_HullDesc3d", "hit", offsetof(VmLub_HullDesc3d, f_hit)},
    {"Lub_HullDesc3d", "sensor_events",
     offsetof(VmLub_HullDesc3d, f_sensor_events)},
    {"Lub_HullDesc3d", "pre_solve", offsetof(VmLub_HullDesc3d, f_pre_solve)},
    {"Lub_HullDesc3d", "filter", offsetof(VmLub_HullDesc3d, f_filter)},
    {"Lub_HullDesc3d", "points", offsetof(VmLub_HullDesc3d, f_points)},
    {"Lub_HullDesc3d", "max_vertices",
     offsetof(VmLub_HullDesc3d, f_max_vertices)},
    {"Lub_SurfaceMaterial3d", "friction",
     offsetof(VmLub_SurfaceMaterial3d, f_friction)},
    {"Lub_SurfaceMaterial3d", "restitution",
     offsetof(VmLub_SurfaceMaterial3d, f_restitution)},
    {"Lub_SurfaceMaterial3d", "material_id",
     offsetof(VmLub_SurfaceMaterial3d, f_material_id)},
    {"Lub_MeshDesc3d", "version", offsetof(VmLub_MeshDesc3d, f_version)},
    {"Lub_MeshDesc3d", "density", offsetof(VmLub_MeshDesc3d, f_density)},
    {"Lub_MeshDesc3d", "friction", offsetof(VmLub_MeshDesc3d, f_friction)},
    {"Lub_MeshDesc3d", "restitution",
     offsetof(VmLub_MeshDesc3d, f_restitution)},
    {"Lub_MeshDesc3d", "tag", offsetof(VmLub_MeshDesc3d, f_tag)},
    {"Lub_MeshDesc3d", "material_name",
     offsetof(VmLub_MeshDesc3d, f_material_name)},
    {"Lub_MeshDesc3d", "material_id",
     offsetof(VmLub_MeshDesc3d, f_material_id)},
    {"Lub_MeshDesc3d", "sensor", offsetof(VmLub_MeshDesc3d, f_sensor)},
    {"Lub_MeshDesc3d", "contact", offsetof(VmLub_MeshDesc3d, f_contact)},
    {"Lub_MeshDesc3d", "hit", offsetof(VmLub_MeshDesc3d, f_hit)},
    {"Lub_MeshDesc3d", "sensor_events",
     offsetof(VmLub_MeshDesc3d, f_sensor_events)},
    {"Lub_MeshDesc3d", "pre_solve", offsetof(VmLub_MeshDesc3d, f_pre_solve)},
    {"Lub_MeshDesc3d", "filter", offsetof(VmLub_MeshDesc3d, f_filter)},
    {"Lub_MeshDesc3d", "positions", offsetof(VmLub_MeshDesc3d, f_positions)},
    {"Lub_MeshDesc3d", "indices", offsetof(VmLub_MeshDesc3d, f_indices)},
    {"Lub_MeshDesc3d", "scale", offsetof(VmLub_MeshDesc3d, f_scale)},
    {"Lub_MeshDesc3d", "weld_vertices",
     offsetof(VmLub_MeshDesc3d, f_weld_vertices)},
    {"Lub_MeshDesc3d", "weld_tolerance",
     offsetof(VmLub_MeshDesc3d, f_weld_tolerance)},
    {"Lub_MeshDesc3d", "use_median_split",
     offsetof(VmLub_MeshDesc3d, f_use_median_split)},
    {"Lub_MeshDesc3d", "identify_edges",
     offsetof(VmLub_MeshDesc3d, f_identify_edges)},
    {"Lub_MeshDesc3d", "materials", offsetof(VmLub_MeshDesc3d, f_materials)},
    {"Lub_MeshDesc3d", "material_indices",
     offsetof(VmLub_MeshDesc3d, f_material_indices)},
    {"Lub_HeightFieldDesc3d", "version",
     offsetof(VmLub_HeightFieldDesc3d, f_version)},
    {"Lub_HeightFieldDesc3d", "density",
     offsetof(VmLub_HeightFieldDesc3d, f_density)},
    {"Lub_HeightFieldDesc3d", "friction",
     offsetof(VmLub_HeightFieldDesc3d, f_friction)},
    {"Lub_HeightFieldDesc3d", "restitution",
     offsetof(VmLub_HeightFieldDesc3d, f_restitution)},
    {"Lub_HeightFieldDesc3d", "tag", offsetof(VmLub_HeightFieldDesc3d, f_tag)},
    {"Lub_HeightFieldDesc3d", "material_name",
     offsetof(VmLub_HeightFieldDesc3d, f_material_name)},
    {"Lub_HeightFieldDesc3d", "material_id",
     offsetof(VmLub_HeightFieldDesc3d, f_material_id)},
    {"Lub_HeightFieldDesc3d", "sensor",
     offsetof(VmLub_HeightFieldDesc3d, f_sensor)},
    {"Lub_HeightFieldDesc3d", "contact",
     offsetof(VmLub_HeightFieldDesc3d, f_contact)},
    {"Lub_HeightFieldDesc3d", "hit", offsetof(VmLub_HeightFieldDesc3d, f_hit)},
    {"Lub_HeightFieldDesc3d", "sensor_events",
     offsetof(VmLub_HeightFieldDesc3d, f_sensor_events)},
    {"Lub_HeightFieldDesc3d", "pre_solve",
     offsetof(VmLub_HeightFieldDesc3d, f_pre_solve)},
    {"Lub_HeightFieldDesc3d", "filter",
     offsetof(VmLub_HeightFieldDesc3d, f_filter)},
    {"Lub_HeightFieldDesc3d", "heights",
     offsetof(VmLub_HeightFieldDesc3d, f_heights)},
    {"Lub_HeightFieldDesc3d", "x_count",
     offsetof(VmLub_HeightFieldDesc3d, f_x_count)},
    {"Lub_HeightFieldDesc3d", "z_count",
     offsetof(VmLub_HeightFieldDesc3d, f_z_count)},
    {"Lub_HeightFieldDesc3d", "cell_width",
     offsetof(VmLub_HeightFieldDesc3d, f_cell_width)},
    {"Lub_HeightFieldDesc3d", "scale",
     offsetof(VmLub_HeightFieldDesc3d, f_scale)},
    {"Lub_HeightFieldDesc3d", "min_height",
     offsetof(VmLub_HeightFieldDesc3d, f_min_height)},
    {"Lub_HeightFieldDesc3d", "max_height",
     offsetof(VmLub_HeightFieldDesc3d, f_max_height)},
    {"Lub_HeightFieldDesc3d", "clockwise_winding",
     offsetof(VmLub_HeightFieldDesc3d, f_clockwise_winding)},
    {"Lub_CompoundSphere3d", "r", offsetof(VmLub_CompoundSphere3d, f_r)},
    {"Lub_CompoundSphere3d", "center",
     offsetof(VmLub_CompoundSphere3d, f_center)},
    {"Lub_CompoundBox3d", "hx", offsetof(VmLub_CompoundBox3d, f_hx)},
    {"Lub_CompoundBox3d", "hy", offsetof(VmLub_CompoundBox3d, f_hy)},
    {"Lub_CompoundBox3d", "hz", offsetof(VmLub_CompoundBox3d, f_hz)},
    {"Lub_CompoundCapsule3d", "a", offsetof(VmLub_CompoundCapsule3d, f_a)},
    {"Lub_CompoundCapsule3d", "b", offsetof(VmLub_CompoundCapsule3d, f_b)},
    {"Lub_CompoundCapsule3d", "r", offsetof(VmLub_CompoundCapsule3d, f_r)},
    {"Lub_CompoundChild3d", "pose", offsetof(VmLub_CompoundChild3d, f_pose)},
    {"Lub_CompoundChild3d", "friction",
     offsetof(VmLub_CompoundChild3d, f_friction)},
    {"Lub_CompoundChild3d", "restitution",
     offsetof(VmLub_CompoundChild3d, f_restitution)},
    {"Lub_CompoundChild3d", "material_id",
     offsetof(VmLub_CompoundChild3d, f_material_id)},
    {"Lub_CompoundChild3d", "sphere",
     offsetof(VmLub_CompoundChild3d, f_sphere)},
    {"Lub_CompoundChild3d", "box", offsetof(VmLub_CompoundChild3d, f_box)},
    {"Lub_CompoundChild3d", "capsule",
     offsetof(VmLub_CompoundChild3d, f_capsule)},
    {"Lub_CompoundDesc3d", "version",
     offsetof(VmLub_CompoundDesc3d, f_version)},
    {"Lub_CompoundDesc3d", "density",
     offsetof(VmLub_CompoundDesc3d, f_density)},
    {"Lub_CompoundDesc3d", "friction",
     offsetof(VmLub_CompoundDesc3d, f_friction)},
    {"Lub_CompoundDesc3d", "restitution",
     offsetof(VmLub_CompoundDesc3d, f_restitution)},
    {"Lub_CompoundDesc3d", "tag", offsetof(VmLub_CompoundDesc3d, f_tag)},
    {"Lub_CompoundDesc3d", "material_name",
     offsetof(VmLub_CompoundDesc3d, f_material_name)},
    {"Lub_CompoundDesc3d", "material_id",
     offsetof(VmLub_CompoundDesc3d, f_material_id)},
    {"Lub_CompoundDesc3d", "sensor", offsetof(VmLub_CompoundDesc3d, f_sensor)},
    {"Lub_CompoundDesc3d", "contact",
     offsetof(VmLub_CompoundDesc3d, f_contact)},
    {"Lub_CompoundDesc3d", "hit", offsetof(VmLub_CompoundDesc3d, f_hit)},
    {"Lub_CompoundDesc3d", "sensor_events",
     offsetof(VmLub_CompoundDesc3d, f_sensor_events)},
    {"Lub_CompoundDesc3d", "pre_solve",
     offsetof(VmLub_CompoundDesc3d, f_pre_solve)},
    {"Lub_CompoundDesc3d", "filter", offsetof(VmLub_CompoundDesc3d, f_filter)},
    {"Lub_CompoundDesc3d", "children",
     offsetof(VmLub_CompoundDesc3d, f_children)},
    {"Lub_CommandOpts3d", "wake", offsetof(VmLub_CommandOpts3d, f_wake)},
    {"Lub_CommandOpts3d", "point", offsetof(VmLub_CommandOpts3d, f_point)},
    {"Lub_VelocityDesc3d", "vx", offsetof(VmLub_VelocityDesc3d, f_vx)},
    {"Lub_VelocityDesc3d", "vy", offsetof(VmLub_VelocityDesc3d, f_vy)},
    {"Lub_VelocityDesc3d", "vz", offsetof(VmLub_VelocityDesc3d, f_vz)},
    {"Lub_VelocityDesc3d", "wx", offsetof(VmLub_VelocityDesc3d, f_wx)},
    {"Lub_VelocityDesc3d", "wy", offsetof(VmLub_VelocityDesc3d, f_wy)},
    {"Lub_VelocityDesc3d", "wz", offsetof(VmLub_VelocityDesc3d, f_wz)},
    {"Lub_PoseDesc3d", "x", offsetof(VmLub_PoseDesc3d, f_x)},
    {"Lub_PoseDesc3d", "y", offsetof(VmLub_PoseDesc3d, f_y)},
    {"Lub_PoseDesc3d", "z", offsetof(VmLub_PoseDesc3d, f_z)},
    {"Lub_PoseDesc3d", "quat", offsetof(VmLub_PoseDesc3d, f_quat)},
    {"Lub_PoseDesc3d", "euler", offsetof(VmLub_PoseDesc3d, f_euler)},
    {"Lub_TargetDesc3d", "x", offsetof(VmLub_TargetDesc3d, f_x)},
    {"Lub_TargetDesc3d", "y", offsetof(VmLub_TargetDesc3d, f_y)},
    {"Lub_TargetDesc3d", "z", offsetof(VmLub_TargetDesc3d, f_z)},
    {"Lub_TargetDesc3d", "quat", offsetof(VmLub_TargetDesc3d, f_quat)},
    {"Lub_TargetDesc3d", "euler", offsetof(VmLub_TargetDesc3d, f_euler)},
    {"Lub_TargetDesc3d", "time_step",
     offsetof(VmLub_TargetDesc3d, f_time_step)},
    {"Lub_TargetDesc3d", "wake", offsetof(VmLub_TargetDesc3d, f_wake)},
    {"Lub_FrameDesc3d", "x", offsetof(VmLub_FrameDesc3d, f_x)},
    {"Lub_FrameDesc3d", "y", offsetof(VmLub_FrameDesc3d, f_y)},
    {"Lub_FrameDesc3d", "z", offsetof(VmLub_FrameDesc3d, f_z)},
    {"Lub_FrameDesc3d", "quat", offsetof(VmLub_FrameDesc3d, f_quat)},
    {"Lub_FrameDesc3d", "euler", offsetof(VmLub_FrameDesc3d, f_euler)},
    {"Lub_JointSpringDesc3d", "enabled",
     offsetof(VmLub_JointSpringDesc3d, f_enabled)},
    {"Lub_JointSpringDesc3d", "hertz",
     offsetof(VmLub_JointSpringDesc3d, f_hertz)},
    {"Lub_JointSpringDesc3d", "damping_ratio",
     offsetof(VmLub_JointSpringDesc3d, f_damping_ratio)},
    {"Lub_JointSpringDesc3d", "linear_hertz",
     offsetof(VmLub_JointSpringDesc3d, f_linear_hertz)},
    {"Lub_JointSpringDesc3d", "linear_damping_ratio",
     offsetof(VmLub_JointSpringDesc3d, f_linear_damping_ratio)},
    {"Lub_JointSpringDesc3d", "angular_hertz",
     offsetof(VmLub_JointSpringDesc3d, f_angular_hertz)},
    {"Lub_JointSpringDesc3d", "angular_damping_ratio",
     offsetof(VmLub_JointSpringDesc3d, f_angular_damping_ratio)},
    {"Lub_JointSpringDesc3d", "max_torque",
     offsetof(VmLub_JointSpringDesc3d, f_max_torque)},
    {"Lub_JointLimitDesc3d", "enabled",
     offsetof(VmLub_JointLimitDesc3d, f_enabled)},
    {"Lub_JointLimitDesc3d", "lower",
     offsetof(VmLub_JointLimitDesc3d, f_lower)},
    {"Lub_JointLimitDesc3d", "upper",
     offsetof(VmLub_JointLimitDesc3d, f_upper)},
    {"Lub_JointLimitDesc3d", "min_length",
     offsetof(VmLub_JointLimitDesc3d, f_min_length)},
    {"Lub_JointLimitDesc3d", "max_length",
     offsetof(VmLub_JointLimitDesc3d, f_max_length)},
    {"Lub_JointLimitDesc3d", "cone_angle",
     offsetof(VmLub_JointLimitDesc3d, f_cone_angle)},
    {"Lub_JointLimitDesc3d", "lower_twist_angle",
     offsetof(VmLub_JointLimitDesc3d, f_lower_twist_angle)},
    {"Lub_JointLimitDesc3d", "upper_twist_angle",
     offsetof(VmLub_JointLimitDesc3d, f_upper_twist_angle)},
    {"Lub_JointMotorDesc3d", "enabled",
     offsetof(VmLub_JointMotorDesc3d, f_enabled)},
    {"Lub_JointMotorDesc3d", "speed",
     offsetof(VmLub_JointMotorDesc3d, f_speed)},
    {"Lub_JointMotorDesc3d", "max_force",
     offsetof(VmLub_JointMotorDesc3d, f_max_force)},
    {"Lub_JointMotorDesc3d", "max_torque",
     offsetof(VmLub_JointMotorDesc3d, f_max_torque)},
    {"Lub_JointMotorDesc3d", "velocity",
     offsetof(VmLub_JointMotorDesc3d, f_velocity)},
    {"Lub_JointMotorDesc3d", "linear_velocity",
     offsetof(VmLub_JointMotorDesc3d, f_linear_velocity)},
    {"Lub_JointMotorDesc3d", "angular_velocity",
     offsetof(VmLub_JointMotorDesc3d, f_angular_velocity)},
    {"Lub_JointMotorDesc3d", "max_velocity_force",
     offsetof(VmLub_JointMotorDesc3d, f_max_velocity_force)},
    {"Lub_JointMotorDesc3d", "max_velocity_torque",
     offsetof(VmLub_JointMotorDesc3d, f_max_velocity_torque)},
    {"Lub_JointTargetDesc3d", "translation",
     offsetof(VmLub_JointTargetDesc3d, f_translation)},
    {"Lub_JointTargetDesc3d", "angle",
     offsetof(VmLub_JointTargetDesc3d, f_angle)},
    {"Lub_JointTargetDesc3d", "steering_angle",
     offsetof(VmLub_JointTargetDesc3d, f_steering_angle)},
    {"Lub_JointTargetDesc3d", "quat",
     offsetof(VmLub_JointTargetDesc3d, f_quat)},
    {"Lub_JointTargetDesc3d", "euler",
     offsetof(VmLub_JointTargetDesc3d, f_euler)},
    {"Lub_JointTargetDesc3d", "linear_velocity",
     offsetof(VmLub_JointTargetDesc3d, f_linear_velocity)},
    {"Lub_JointTargetDesc3d", "angular_velocity",
     offsetof(VmLub_JointTargetDesc3d, f_angular_velocity)},
    {"Lub_JointDesc3d", "version", offsetof(VmLub_JointDesc3d, f_version)},
    {"Lub_JointDesc3d", "type", offsetof(VmLub_JointDesc3d, f_type)},
    {"Lub_JointDesc3d", "body_a", offsetof(VmLub_JointDesc3d, f_body_a)},
    {"Lub_JointDesc3d", "body_b", offsetof(VmLub_JointDesc3d, f_body_b)},
    {"Lub_JointDesc3d", "anchor_a", offsetof(VmLub_JointDesc3d, f_anchor_a)},
    {"Lub_JointDesc3d", "anchor_b", offsetof(VmLub_JointDesc3d, f_anchor_b)},
    {"Lub_JointDesc3d", "axis", offsetof(VmLub_JointDesc3d, f_axis)},
    {"Lub_JointDesc3d", "frame_a", offsetof(VmLub_JointDesc3d, f_frame_a)},
    {"Lub_JointDesc3d", "frame_b", offsetof(VmLub_JointDesc3d, f_frame_b)},
    {"Lub_JointDesc3d", "collide_connected",
     offsetof(VmLub_JointDesc3d, f_collide_connected)},
    {"Lub_JointDesc3d", "force_threshold",
     offsetof(VmLub_JointDesc3d, f_force_threshold)},
    {"Lub_JointDesc3d", "torque_threshold",
     offsetof(VmLub_JointDesc3d, f_torque_threshold)},
    {"Lub_JointDesc3d", "constraint_hertz",
     offsetof(VmLub_JointDesc3d, f_constraint_hertz)},
    {"Lub_JointDesc3d", "constraint_damping_ratio",
     offsetof(VmLub_JointDesc3d, f_constraint_damping_ratio)},
    {"Lub_JointDesc3d", "length", offsetof(VmLub_JointDesc3d, f_length)},
    {"Lub_JointDesc3d", "min_length",
     offsetof(VmLub_JointDesc3d, f_min_length)},
    {"Lub_JointDesc3d", "max_length",
     offsetof(VmLub_JointDesc3d, f_max_length)},
    {"Lub_JointDesc3d", "lower", offsetof(VmLub_JointDesc3d, f_lower)},
    {"Lub_JointDesc3d", "upper", offsetof(VmLub_JointDesc3d, f_upper)},
    {"Lub_JointDesc3d", "hertz", offsetof(VmLub_JointDesc3d, f_hertz)},
    {"Lub_JointDesc3d", "damping_ratio",
     offsetof(VmLub_JointDesc3d, f_damping_ratio)},
    {"Lub_JointDesc3d", "linear_hertz",
     offsetof(VmLub_JointDesc3d, f_linear_hertz)},
    {"Lub_JointDesc3d", "angular_hertz",
     offsetof(VmLub_JointDesc3d, f_angular_hertz)},
    {"Lub_JointDesc3d", "linear_damping_ratio",
     offsetof(VmLub_JointDesc3d, f_linear_damping_ratio)},
    {"Lub_JointDesc3d", "angular_damping_ratio",
     offsetof(VmLub_JointDesc3d, f_angular_damping_ratio)},
    {"Lub_JointDesc3d", "max_force", offsetof(VmLub_JointDesc3d, f_max_force)},
    {"Lub_JointDesc3d", "max_torque",
     offsetof(VmLub_JointDesc3d, f_max_torque)},
    {"Lub_JointDesc3d", "max_velocity_force",
     offsetof(VmLub_JointDesc3d, f_max_velocity_force)},
    {"Lub_JointDesc3d", "max_velocity_torque",
     offsetof(VmLub_JointDesc3d, f_max_velocity_torque)},
    {"Lub_JointDesc3d", "max_spring_force",
     offsetof(VmLub_JointDesc3d, f_max_spring_force)},
    {"Lub_JointDesc3d", "max_spring_torque",
     offsetof(VmLub_JointDesc3d, f_max_spring_torque)},
    {"Lub_JointDesc3d", "motor_speed",
     offsetof(VmLub_JointDesc3d, f_motor_speed)},
    {"Lub_JointDesc3d", "target_angle",
     offsetof(VmLub_JointDesc3d, f_target_angle)},
    {"Lub_JointDesc3d", "target_translation",
     offsetof(VmLub_JointDesc3d, f_target_translation)},
    {"Lub_JointDesc3d", "target_rotation",
     offsetof(VmLub_JointDesc3d, f_target_rotation)},
    {"Lub_JointDesc3d", "linear_velocity",
     offsetof(VmLub_JointDesc3d, f_linear_velocity)},
    {"Lub_JointDesc3d", "angular_velocity",
     offsetof(VmLub_JointDesc3d, f_angular_velocity)},
    {"Lub_JointDesc3d", "motor_velocity",
     offsetof(VmLub_JointDesc3d, f_motor_velocity)},
    {"Lub_JointDesc3d", "enable_spring",
     offsetof(VmLub_JointDesc3d, f_enable_spring)},
    {"Lub_JointDesc3d", "enable_limit",
     offsetof(VmLub_JointDesc3d, f_enable_limit)},
    {"Lub_JointDesc3d", "enable_motor",
     offsetof(VmLub_JointDesc3d, f_enable_motor)},
    {"Lub_JointDesc3d", "cone_angle",
     offsetof(VmLub_JointDesc3d, f_cone_angle)},
    {"Lub_JointDesc3d", "enable_cone_limit",
     offsetof(VmLub_JointDesc3d, f_enable_cone_limit)},
    {"Lub_JointDesc3d", "enable_twist_limit",
     offsetof(VmLub_JointDesc3d, f_enable_twist_limit)},
    {"Lub_JointDesc3d", "lower_twist_angle",
     offsetof(VmLub_JointDesc3d, f_lower_twist_angle)},
    {"Lub_JointDesc3d", "upper_twist_angle",
     offsetof(VmLub_JointDesc3d, f_upper_twist_angle)},
    {"Lub_JointDesc3d", "spring", offsetof(VmLub_JointDesc3d, f_spring)},
    {"Lub_JointDesc3d", "limit", offsetof(VmLub_JointDesc3d, f_limit)},
    {"Lub_JointDesc3d", "motor", offsetof(VmLub_JointDesc3d, f_motor)},
    {"Lub_MaterialDesc3d", "density",
     offsetof(VmLub_MaterialDesc3d, f_density)},
    {"Lub_MaterialDesc3d", "friction",
     offsetof(VmLub_MaterialDesc3d, f_friction)},
    {"Lub_MaterialDesc3d", "restitution",
     offsetof(VmLub_MaterialDesc3d, f_restitution)},
    {"Lub_MaterialDesc3d", "material_name",
     offsetof(VmLub_MaterialDesc3d, f_material_name)},
    {"Lub_MaterialDesc3d", "material_id",
     offsetof(VmLub_MaterialDesc3d, f_material_id)},
    {"Lub_ShapeEventsDesc3d", "sensor_events",
     offsetof(VmLub_ShapeEventsDesc3d, f_sensor_events)},
    {"Lub_ShapeEventsDesc3d", "contact",
     offsetof(VmLub_ShapeEventsDesc3d, f_contact)},
    {"Lub_ShapeEventsDesc3d", "pre_solve",
     offsetof(VmLub_ShapeEventsDesc3d, f_pre_solve)},
    {"Lub_ShapeEventsDesc3d", "hit", offsetof(VmLub_ShapeEventsDesc3d, f_hit)},
    {"Lub_MoverDesc3d", "a", offsetof(VmLub_MoverDesc3d, f_a)},
    {"Lub_MoverDesc3d", "b", offsetof(VmLub_MoverDesc3d, f_b)},
    {"Lub_MoverDesc3d", "r", offsetof(VmLub_MoverDesc3d, f_r)},
    {"Lub_MoverDesc3d", "dx", offsetof(VmLub_MoverDesc3d, f_dx)},
    {"Lub_MoverDesc3d", "dy", offsetof(VmLub_MoverDesc3d, f_dy)},
    {"Lub_MoverDesc3d", "dz", offsetof(VmLub_MoverDesc3d, f_dz)},
    {"Lub_MoverDesc3d", "max_fraction",
     offsetof(VmLub_MoverDesc3d, f_max_fraction)},
    {"Lub_MoverDesc3d", "filter", offsetof(VmLub_MoverDesc3d, f_filter)},
    {"Lub_RaycastDesc3d", "x", offsetof(VmLub_RaycastDesc3d, f_x)},
    {"Lub_RaycastDesc3d", "y", offsetof(VmLub_RaycastDesc3d, f_y)},
    {"Lub_RaycastDesc3d", "z", offsetof(VmLub_RaycastDesc3d, f_z)},
    {"Lub_RaycastDesc3d", "dx", offsetof(VmLub_RaycastDesc3d, f_dx)},
    {"Lub_RaycastDesc3d", "dy", offsetof(VmLub_RaycastDesc3d, f_dy)},
    {"Lub_RaycastDesc3d", "dz", offsetof(VmLub_RaycastDesc3d, f_dz)},
    {"Lub_RaycastDesc3d", "max_fraction",
     offsetof(VmLub_RaycastDesc3d, f_max_fraction)},
    {"Lub_RaycastDesc3d", "filter", offsetof(VmLub_RaycastDesc3d, f_filter)},
    {"Lub_AabbDesc3d", "min_x", offsetof(VmLub_AabbDesc3d, f_min_x)},
    {"Lub_AabbDesc3d", "min_y", offsetof(VmLub_AabbDesc3d, f_min_y)},
    {"Lub_AabbDesc3d", "min_z", offsetof(VmLub_AabbDesc3d, f_min_z)},
    {"Lub_AabbDesc3d", "max_x", offsetof(VmLub_AabbDesc3d, f_max_x)},
    {"Lub_AabbDesc3d", "max_y", offsetof(VmLub_AabbDesc3d, f_max_y)},
    {"Lub_AabbDesc3d", "max_z", offsetof(VmLub_AabbDesc3d, f_max_z)},
    {"Lub_AabbDesc3d", "filter", offsetof(VmLub_AabbDesc3d, f_filter)},
    {"Lub_SphereProxy3d", "r", offsetof(VmLub_SphereProxy3d, f_r)},
    {"Lub_SphereProxy3d", "center", offsetof(VmLub_SphereProxy3d, f_center)},
    {"Lub_BoxProxy3d", "hx", offsetof(VmLub_BoxProxy3d, f_hx)},
    {"Lub_BoxProxy3d", "hy", offsetof(VmLub_BoxProxy3d, f_hy)},
    {"Lub_BoxProxy3d", "hz", offsetof(VmLub_BoxProxy3d, f_hz)},
    {"Lub_BoxProxy3d", "radius", offsetof(VmLub_BoxProxy3d, f_radius)},
    {"Lub_BoxProxy3d", "center", offsetof(VmLub_BoxProxy3d, f_center)},
    {"Lub_BoxProxy3d", "quat", offsetof(VmLub_BoxProxy3d, f_quat)},
    {"Lub_CapsuleProxy3d", "a", offsetof(VmLub_CapsuleProxy3d, f_a)},
    {"Lub_CapsuleProxy3d", "b", offsetof(VmLub_CapsuleProxy3d, f_b)},
    {"Lub_CapsuleProxy3d", "r", offsetof(VmLub_CapsuleProxy3d, f_r)},
    {"Lub_ShapeProxyDesc3d", "sphere",
     offsetof(VmLub_ShapeProxyDesc3d, f_sphere)},
    {"Lub_ShapeProxyDesc3d", "box", offsetof(VmLub_ShapeProxyDesc3d, f_box)},
    {"Lub_ShapeProxyDesc3d", "capsule",
     offsetof(VmLub_ShapeProxyDesc3d, f_capsule)},
    {"Lub_ShapeProxyDesc3d", "dx", offsetof(VmLub_ShapeProxyDesc3d, f_dx)},
    {"Lub_ShapeProxyDesc3d", "dy", offsetof(VmLub_ShapeProxyDesc3d, f_dy)},
    {"Lub_ShapeProxyDesc3d", "dz", offsetof(VmLub_ShapeProxyDesc3d, f_dz)},
    {"Lub_ShapeProxyDesc3d", "max_fraction",
     offsetof(VmLub_ShapeProxyDesc3d, f_max_fraction)},
    {"Lub_ShapeProxyDesc3d", "filter",
     offsetof(VmLub_ShapeProxyDesc3d, f_filter)},
    {"Lub_Pose3d", "x", offsetof(VmLub_Pose3d, f_x)},
    {"Lub_Pose3d", "y", offsetof(VmLub_Pose3d, f_y)},
    {"Lub_Pose3d", "z", offsetof(VmLub_Pose3d, f_z)},
    {"Lub_Pose3d", "qx", offsetof(VmLub_Pose3d, f_qx)},
    {"Lub_Pose3d", "qy", offsetof(VmLub_Pose3d, f_qy)},
    {"Lub_Pose3d", "qz", offsetof(VmLub_Pose3d, f_qz)},
    {"Lub_Pose3d", "qw", offsetof(VmLub_Pose3d, f_qw)},
    {"Lub_Pose3d", "vx", offsetof(VmLub_Pose3d, f_vx)},
    {"Lub_Pose3d", "vy", offsetof(VmLub_Pose3d, f_vy)},
    {"Lub_Pose3d", "vz", offsetof(VmLub_Pose3d, f_vz)},
    {"Lub_Pose3d", "wx", offsetof(VmLub_Pose3d, f_wx)},
    {"Lub_Pose3d", "wy", offsetof(VmLub_Pose3d, f_wy)},
    {"Lub_Pose3d", "wz", offsetof(VmLub_Pose3d, f_wz)},
    {"Lub_Pose3d", "awake", offsetof(VmLub_Pose3d, f_awake)},
    {"Lub_Pose3d", "enabled", offsetof(VmLub_Pose3d, f_enabled)},
    {"Lub_Pose3d", "sleep", offsetof(VmLub_Pose3d, f_sleep)},
    {"Lub_Pose3d", "sleep_threshold",
     offsetof(VmLub_Pose3d, f_sleep_threshold)},
    {"Lub_Velocity3d", "x", offsetof(VmLub_Velocity3d, f_x)},
    {"Lub_Velocity3d", "y", offsetof(VmLub_Velocity3d, f_y)},
    {"Lub_Velocity3d", "z", offsetof(VmLub_Velocity3d, f_z)},
    {"Lub_Velocity3d", "wx", offsetof(VmLub_Velocity3d, f_wx)},
    {"Lub_Velocity3d", "wy", offsetof(VmLub_Velocity3d, f_wy)},
    {"Lub_Velocity3d", "wz", offsetof(VmLub_Velocity3d, f_wz)},
    {"Lub_Inertia3d", "xx", offsetof(VmLub_Inertia3d, f_xx)},
    {"Lub_Inertia3d", "yy", offsetof(VmLub_Inertia3d, f_yy)},
    {"Lub_Inertia3d", "zz", offsetof(VmLub_Inertia3d, f_zz)},
    {"Lub_Inertia3d", "xy", offsetof(VmLub_Inertia3d, f_xy)},
    {"Lub_Inertia3d", "xz", offsetof(VmLub_Inertia3d, f_xz)},
    {"Lub_Inertia3d", "yz", offsetof(VmLub_Inertia3d, f_yz)},
    {"Lub_MassData3d", "mass", offsetof(VmLub_MassData3d, f_mass)},
    {"Lub_MassData3d", "center", offsetof(VmLub_MassData3d, f_center)},
    {"Lub_MassData3d", "local_center",
     offsetof(VmLub_MassData3d, f_local_center)},
    {"Lub_MassData3d", "inertia", offsetof(VmLub_MassData3d, f_inertia)},
    {"Lub_Aabb3d", "min_x", offsetof(VmLub_Aabb3d, f_min_x)},
    {"Lub_Aabb3d", "min_y", offsetof(VmLub_Aabb3d, f_min_y)},
    {"Lub_Aabb3d", "min_z", offsetof(VmLub_Aabb3d, f_min_z)},
    {"Lub_Aabb3d", "max_x", offsetof(VmLub_Aabb3d, f_max_x)},
    {"Lub_Aabb3d", "max_y", offsetof(VmLub_Aabb3d, f_max_y)},
    {"Lub_Aabb3d", "max_z", offsetof(VmLub_Aabb3d, f_max_z)},
    {"Lub_ShapeInfo3d", "body", offsetof(VmLub_ShapeInfo3d, f_body)},
    {"Lub_ShapeInfo3d", "shape", offsetof(VmLub_ShapeInfo3d, f_shape)},
    {"Lub_ShapeInfo3d", "tag", offsetof(VmLub_ShapeInfo3d, f_tag)},
    {"Lub_ShapeInfo3d", "material_name",
     offsetof(VmLub_ShapeInfo3d, f_material_name)},
    {"Lub_ShapeInfo3d", "material_id",
     offsetof(VmLub_ShapeInfo3d, f_material_id)},
    {"Lub_ShapeInfo3d", "kind", offsetof(VmLub_ShapeInfo3d, f_kind)},
    {"Lub_ShapeInfo3d", "category_bits",
     offsetof(VmLub_ShapeInfo3d, f_category_bits)},
    {"Lub_ShapeInfo3d", "mask_bits", offsetof(VmLub_ShapeInfo3d, f_mask_bits)},
    {"Lub_ShapeInfo3d", "group", offsetof(VmLub_ShapeInfo3d, f_group)},
    {"Lub_ShapeInfo3d", "valid", offsetof(VmLub_ShapeInfo3d, f_valid)},
    {"Lub_ShapeInfo3d", "density", offsetof(VmLub_ShapeInfo3d, f_density)},
    {"Lub_ShapeInfo3d", "friction", offsetof(VmLub_ShapeInfo3d, f_friction)},
    {"Lub_ShapeInfo3d", "restitution",
     offsetof(VmLub_ShapeInfo3d, f_restitution)},
    {"Lub_ShapeInfo3d", "sensor", offsetof(VmLub_ShapeInfo3d, f_sensor)},
    {"Lub_ShapeInfo3d", "sensor_events",
     offsetof(VmLub_ShapeInfo3d, f_sensor_events)},
    {"Lub_ShapeInfo3d", "contact", offsetof(VmLub_ShapeInfo3d, f_contact)},
    {"Lub_ShapeInfo3d", "pre_solve", offsetof(VmLub_ShapeInfo3d, f_pre_solve)},
    {"Lub_ShapeInfo3d", "hit", offsetof(VmLub_ShapeInfo3d, f_hit)},
    {"Lub_ShapeInfo3d", "filter", offsetof(VmLub_ShapeInfo3d, f_filter)},
    {"Lub_ShapeInfo3d", "aabb", offsetof(VmLub_ShapeInfo3d, f_aabb)},
    {"Lub_WorldInfo3d", "key", offsetof(VmLub_WorldInfo3d, f_key)},
    {"Lub_WorldInfo3d", "valid", offsetof(VmLub_WorldInfo3d, f_valid)},
    {"Lub_WorldInfo3d", "version", offsetof(VmLub_WorldInfo3d, f_version)},
    {"Lub_WorldInfo3d", "generation",
     offsetof(VmLub_WorldInfo3d, f_generation)},
    {"Lub_WorldInfo3d", "begun", offsetof(VmLub_WorldInfo3d, f_begun)},
    {"Lub_WorldInfo3d", "prune", offsetof(VmLub_WorldInfo3d, f_prune)},
    {"Lub_WorldInfo3d", "fixed_dt", offsetof(VmLub_WorldInfo3d, f_fixed_dt)},
    {"Lub_WorldInfo3d", "substeps", offsetof(VmLub_WorldInfo3d, f_substeps)},
    {"Lub_WorldInfo3d", "max_steps", offsetof(VmLub_WorldInfo3d, f_max_steps)},
    {"Lub_WorldInfo3d", "accumulator",
     offsetof(VmLub_WorldInfo3d, f_accumulator)},
    {"Lub_WorldInfo3d", "pending_commands",
     offsetof(VmLub_WorldInfo3d, f_pending_commands)},
    {"Lub_WorldInfo3d", "gravity", offsetof(VmLub_WorldInfo3d, f_gravity)},
    {"Lub_WorldInfo3d", "sleep", offsetof(VmLub_WorldInfo3d, f_sleep)},
    {"Lub_WorldInfo3d", "continuous",
     offsetof(VmLub_WorldInfo3d, f_continuous)},
    {"Lub_WorldInfo3d", "warm_starting",
     offsetof(VmLub_WorldInfo3d, f_warm_starting)},
    {"Lub_WorldInfo3d", "restitution_threshold",
     offsetof(VmLub_WorldInfo3d, f_restitution_threshold)},
    {"Lub_WorldInfo3d", "hit_event_threshold",
     offsetof(VmLub_WorldInfo3d, f_hit_event_threshold)},
    {"Lub_WorldInfo3d", "maximum_linear_speed",
     offsetof(VmLub_WorldInfo3d, f_maximum_linear_speed)},
    {"Lub_WorldInfo3d", "awake_body_count",
     offsetof(VmLub_WorldInfo3d, f_awake_body_count)},
    {"Lub_StepInfo3d", "steps", offsetof(VmLub_StepInfo3d, f_steps)},
    {"Lub_StepInfo3d", "commands", offsetof(VmLub_StepInfo3d, f_commands)},
    {"Lub_StepInfo3d", "alpha", offsetof(VmLub_StepInfo3d, f_alpha)},
    {"Lub_StepInfo3d", "dropped", offsetof(VmLub_StepInfo3d, f_dropped)},
    {"Lub_StepInfo3d", "contact_begins",
     offsetof(VmLub_StepInfo3d, f_contact_begins)},
    {"Lub_StepInfo3d", "contact_ends",
     offsetof(VmLub_StepInfo3d, f_contact_ends)},
    {"Lub_StepInfo3d", "contact_hits",
     offsetof(VmLub_StepInfo3d, f_contact_hits)},
    {"Lub_StepInfo3d", "sensor_begins",
     offsetof(VmLub_StepInfo3d, f_sensor_begins)},
    {"Lub_StepInfo3d", "sensor_ends",
     offsetof(VmLub_StepInfo3d, f_sensor_ends)},
    {"Lub_StepInfo3d", "body_moves", offsetof(VmLub_StepInfo3d, f_body_moves)},
    {"Lub_StepInfo3d", "body_events",
     offsetof(VmLub_StepInfo3d, f_body_events)},
    {"Lub_StepInfo3d", "joint_events",
     offsetof(VmLub_StepInfo3d, f_joint_events)},
    {"Lub_Frame3d", "x", offsetof(VmLub_Frame3d, f_x)},
    {"Lub_Frame3d", "y", offsetof(VmLub_Frame3d, f_y)},
    {"Lub_Frame3d", "z", offsetof(VmLub_Frame3d, f_z)},
    {"Lub_Frame3d", "qx", offsetof(VmLub_Frame3d, f_qx)},
    {"Lub_Frame3d", "qy", offsetof(VmLub_Frame3d, f_qy)},
    {"Lub_Frame3d", "qz", offsetof(VmLub_Frame3d, f_qz)},
    {"Lub_Frame3d", "qw", offsetof(VmLub_Frame3d, f_qw)},
    {"Lub_JointView3d", "joint", offsetof(VmLub_JointView3d, f_joint)},
    {"Lub_JointView3d", "type", offsetof(VmLub_JointView3d, f_type)},
    {"Lub_JointView3d", "a", offsetof(VmLub_JointView3d, f_a)},
    {"Lub_JointView3d", "b", offsetof(VmLub_JointView3d, f_b)},
    {"Lub_JointView3d", "valid", offsetof(VmLub_JointView3d, f_valid)},
    {"Lub_JointInfo3d", "joint", offsetof(VmLub_JointInfo3d, f_joint)},
    {"Lub_JointInfo3d", "type", offsetof(VmLub_JointInfo3d, f_type)},
    {"Lub_JointInfo3d", "a", offsetof(VmLub_JointInfo3d, f_a)},
    {"Lub_JointInfo3d", "b", offsetof(VmLub_JointInfo3d, f_b)},
    {"Lub_JointInfo3d", "valid", offsetof(VmLub_JointInfo3d, f_valid)},
    {"Lub_JointInfo3d", "collide_connected",
     offsetof(VmLub_JointInfo3d, f_collide_connected)},
    {"Lub_JointInfo3d", "force", offsetof(VmLub_JointInfo3d, f_force)},
    {"Lub_JointInfo3d", "torque", offsetof(VmLub_JointInfo3d, f_torque)},
    {"Lub_JointInfo3d", "linear_separation",
     offsetof(VmLub_JointInfo3d, f_linear_separation)},
    {"Lub_JointInfo3d", "angular_separation",
     offsetof(VmLub_JointInfo3d, f_angular_separation)},
    {"Lub_JointInfo3d", "local_frame_a",
     offsetof(VmLub_JointInfo3d, f_local_frame_a)},
    {"Lub_JointInfo3d", "local_frame_b",
     offsetof(VmLub_JointInfo3d, f_local_frame_b)},
    {"Lub_ContactData3d", "a", offsetof(VmLub_ContactData3d, f_a)},
    {"Lub_ContactData3d", "b", offsetof(VmLub_ContactData3d, f_b)},
    {"Lub_ContactData3d", "nx", offsetof(VmLub_ContactData3d, f_nx)},
    {"Lub_ContactData3d", "ny", offsetof(VmLub_ContactData3d, f_ny)},
    {"Lub_ContactData3d", "nz", offsetof(VmLub_ContactData3d, f_nz)},
    {"Lub_ContactData3d", "manifold_count",
     offsetof(VmLub_ContactData3d, f_manifold_count)},
    {"Lub_ContactData3d", "point_count",
     offsetof(VmLub_ContactData3d, f_point_count)},
    {"Lub_ContactData3d", "x", offsetof(VmLub_ContactData3d, f_x)},
    {"Lub_ContactData3d", "y", offsetof(VmLub_ContactData3d, f_y)},
    {"Lub_ContactData3d", "z", offsetof(VmLub_ContactData3d, f_z)},
    {"Lub_ContactData3d", "separation",
     offsetof(VmLub_ContactData3d, f_separation)},
    {"Lub_ContactEvent3d", "a", offsetof(VmLub_ContactEvent3d, f_a)},
    {"Lub_ContactEvent3d", "b", offsetof(VmLub_ContactEvent3d, f_b)},
    {"Lub_ContactEvent3d", "nx", offsetof(VmLub_ContactEvent3d, f_nx)},
    {"Lub_ContactEvent3d", "ny", offsetof(VmLub_ContactEvent3d, f_ny)},
    {"Lub_ContactEvent3d", "nz", offsetof(VmLub_ContactEvent3d, f_nz)},
    {"Lub_ContactEvent3d", "point_count",
     offsetof(VmLub_ContactEvent3d, f_point_count)},
    {"Lub_ContactEvent3d", "x", offsetof(VmLub_ContactEvent3d, f_x)},
    {"Lub_ContactEvent3d", "y", offsetof(VmLub_ContactEvent3d, f_y)},
    {"Lub_ContactEvent3d", "z", offsetof(VmLub_ContactEvent3d, f_z)},
    {"Lub_ContactEvent3d", "approach_speed",
     offsetof(VmLub_ContactEvent3d, f_approach_speed)},
    {"Lub_SensorEvent3d", "sensor", offsetof(VmLub_SensorEvent3d, f_sensor)},
    {"Lub_SensorEvent3d", "visitor", offsetof(VmLub_SensorEvent3d, f_visitor)},
    {"Lub_BodyEvent3d", "body", offsetof(VmLub_BodyEvent3d, f_body)},
    {"Lub_BodyEvent3d", "valid", offsetof(VmLub_BodyEvent3d, f_valid)},
    {"Lub_BodyEvent3d", "x", offsetof(VmLub_BodyEvent3d, f_x)},
    {"Lub_BodyEvent3d", "y", offsetof(VmLub_BodyEvent3d, f_y)},
    {"Lub_BodyEvent3d", "z", offsetof(VmLub_BodyEvent3d, f_z)},
    {"Lub_BodyEvent3d", "qx", offsetof(VmLub_BodyEvent3d, f_qx)},
    {"Lub_BodyEvent3d", "qy", offsetof(VmLub_BodyEvent3d, f_qy)},
    {"Lub_BodyEvent3d", "qz", offsetof(VmLub_BodyEvent3d, f_qz)},
    {"Lub_BodyEvent3d", "qw", offsetof(VmLub_BodyEvent3d, f_qw)},
    {"Lub_BodyEvent3d", "fell_asleep",
     offsetof(VmLub_BodyEvent3d, f_fell_asleep)},
    {"Lub_JointEvent3d", "joint", offsetof(VmLub_JointEvent3d, f_joint)},
    {"Lub_JointEvent3d", "type", offsetof(VmLub_JointEvent3d, f_type)},
    {"Lub_JointEvent3d", "a", offsetof(VmLub_JointEvent3d, f_a)},
    {"Lub_JointEvent3d", "b", offsetof(VmLub_JointEvent3d, f_b)},
    {"Lub_JointEvent3d", "valid", offsetof(VmLub_JointEvent3d, f_valid)},
    {"Lub_RayHit3d", "body", offsetof(VmLub_RayHit3d, f_body)},
    {"Lub_RayHit3d", "shape", offsetof(VmLub_RayHit3d, f_shape)},
    {"Lub_RayHit3d", "tag", offsetof(VmLub_RayHit3d, f_tag)},
    {"Lub_RayHit3d", "material_name",
     offsetof(VmLub_RayHit3d, f_material_name)},
    {"Lub_RayHit3d", "material_id", offsetof(VmLub_RayHit3d, f_material_id)},
    {"Lub_RayHit3d", "kind", offsetof(VmLub_RayHit3d, f_kind)},
    {"Lub_RayHit3d", "category_bits",
     offsetof(VmLub_RayHit3d, f_category_bits)},
    {"Lub_RayHit3d", "mask_bits", offsetof(VmLub_RayHit3d, f_mask_bits)},
    {"Lub_RayHit3d", "group", offsetof(VmLub_RayHit3d, f_group)},
    {"Lub_RayHit3d", "valid", offsetof(VmLub_RayHit3d, f_valid)},
    {"Lub_RayHit3d", "x", offsetof(VmLub_RayHit3d, f_x)},
    {"Lub_RayHit3d", "y", offsetof(VmLub_RayHit3d, f_y)},
    {"Lub_RayHit3d", "z", offsetof(VmLub_RayHit3d, f_z)},
    {"Lub_RayHit3d", "nx", offsetof(VmLub_RayHit3d, f_nx)},
    {"Lub_RayHit3d", "ny", offsetof(VmLub_RayHit3d, f_ny)},
    {"Lub_RayHit3d", "nz", offsetof(VmLub_RayHit3d, f_nz)},
    {"Lub_RayHit3d", "fraction", offsetof(VmLub_RayHit3d, f_fraction)},
    {"Lub_RayHit3d", "hit_material_id",
     offsetof(VmLub_RayHit3d, f_hit_material_id)},
    {"Lub_RayHit3d", "triangle_index",
     offsetof(VmLub_RayHit3d, f_triangle_index)},
    {"Lub_RayHit3d", "child_index", offsetof(VmLub_RayHit3d, f_child_index)},
    {"Lub_RayHit3d", "node_visits", offsetof(VmLub_RayHit3d, f_node_visits)},
    {"Lub_RayHit3d", "leaf_visits", offsetof(VmLub_RayHit3d, f_leaf_visits)},
    {"Lub_ShapeRayHit3d", "x", offsetof(VmLub_ShapeRayHit3d, f_x)},
    {"Lub_ShapeRayHit3d", "y", offsetof(VmLub_ShapeRayHit3d, f_y)},
    {"Lub_ShapeRayHit3d", "z", offsetof(VmLub_ShapeRayHit3d, f_z)},
    {"Lub_ShapeRayHit3d", "nx", offsetof(VmLub_ShapeRayHit3d, f_nx)},
    {"Lub_ShapeRayHit3d", "ny", offsetof(VmLub_ShapeRayHit3d, f_ny)},
    {"Lub_ShapeRayHit3d", "nz", offsetof(VmLub_ShapeRayHit3d, f_nz)},
    {"Lub_ShapeRayHit3d", "fraction",
     offsetof(VmLub_ShapeRayHit3d, f_fraction)},
    {"Lub_ShapeRayHit3d", "iterations",
     offsetof(VmLub_ShapeRayHit3d, f_iterations)},
    {"Lub_ShapeRayHit3d", "triangle_index",
     offsetof(VmLub_ShapeRayHit3d, f_triangle_index)},
    {"Lub_ShapeRayHit3d", "child_index",
     offsetof(VmLub_ShapeRayHit3d, f_child_index)},
    {"Lub_MoverCast3d", "fraction", offsetof(VmLub_MoverCast3d, f_fraction)},
    {"Lub_MoverCast3d", "dx", offsetof(VmLub_MoverCast3d, f_dx)},
    {"Lub_MoverCast3d", "dy", offsetof(VmLub_MoverCast3d, f_dy)},
    {"Lub_MoverCast3d", "dz", offsetof(VmLub_MoverCast3d, f_dz)},
    {"Lub_MoverPlane3d", "body", offsetof(VmLub_MoverPlane3d, f_body)},
    {"Lub_MoverPlane3d", "shape", offsetof(VmLub_MoverPlane3d, f_shape)},
    {"Lub_MoverPlane3d", "tag", offsetof(VmLub_MoverPlane3d, f_tag)},
    {"Lub_MoverPlane3d", "material_name",
     offsetof(VmLub_MoverPlane3d, f_material_name)},
    {"Lub_MoverPlane3d", "material_id",
     offsetof(VmLub_MoverPlane3d, f_material_id)},
    {"Lub_MoverPlane3d", "kind", offsetof(VmLub_MoverPlane3d, f_kind)},
    {"Lub_MoverPlane3d", "category_bits",
     offsetof(VmLub_MoverPlane3d, f_category_bits)},
    {"Lub_MoverPlane3d", "mask_bits",
     offsetof(VmLub_MoverPlane3d, f_mask_bits)},
    {"Lub_MoverPlane3d", "group", offsetof(VmLub_MoverPlane3d, f_group)},
    {"Lub_MoverPlane3d", "valid", offsetof(VmLub_MoverPlane3d, f_valid)},
    {"Lub_MoverPlane3d", "x", offsetof(VmLub_MoverPlane3d, f_x)},
    {"Lub_MoverPlane3d", "y", offsetof(VmLub_MoverPlane3d, f_y)},
    {"Lub_MoverPlane3d", "z", offsetof(VmLub_MoverPlane3d, f_z)},
    {"Lub_MoverPlane3d", "nx", offsetof(VmLub_MoverPlane3d, f_nx)},
    {"Lub_MoverPlane3d", "ny", offsetof(VmLub_MoverPlane3d, f_ny)},
    {"Lub_MoverPlane3d", "nz", offsetof(VmLub_MoverPlane3d, f_nz)},
    {"Lub_MoverPlane3d", "offset", offsetof(VmLub_MoverPlane3d, f_offset)},
    {"Lub_MoverPlane3d", "plane_count",
     offsetof(VmLub_MoverPlane3d, f_plane_count)},
    {"Lub_Profile3d", "step", offsetof(VmLub_Profile3d, f_step)},
    {"Lub_Profile3d", "pairs", offsetof(VmLub_Profile3d, f_pairs)},
    {"Lub_Profile3d", "collide", offsetof(VmLub_Profile3d, f_collide)},
    {"Lub_Profile3d", "solve", offsetof(VmLub_Profile3d, f_solve)},
    {"Lub_Profile3d", "solver_setup",
     offsetof(VmLub_Profile3d, f_solver_setup)},
    {"Lub_Profile3d", "constraints", offsetof(VmLub_Profile3d, f_constraints)},
    {"Lub_Profile3d", "prepare_constraints",
     offsetof(VmLub_Profile3d, f_prepare_constraints)},
    {"Lub_Profile3d", "integrate_velocities",
     offsetof(VmLub_Profile3d, f_integrate_velocities)},
    {"Lub_Profile3d", "warm_start", offsetof(VmLub_Profile3d, f_warm_start)},
    {"Lub_Profile3d", "solve_impulses",
     offsetof(VmLub_Profile3d, f_solve_impulses)},
    {"Lub_Profile3d", "integrate_positions",
     offsetof(VmLub_Profile3d, f_integrate_positions)},
    {"Lub_Profile3d", "relax_impulses",
     offsetof(VmLub_Profile3d, f_relax_impulses)},
    {"Lub_Profile3d", "apply_restitution",
     offsetof(VmLub_Profile3d, f_apply_restitution)},
    {"Lub_Profile3d", "store_impulses",
     offsetof(VmLub_Profile3d, f_store_impulses)},
    {"Lub_Profile3d", "split_islands",
     offsetof(VmLub_Profile3d, f_split_islands)},
    {"Lub_Profile3d", "transforms", offsetof(VmLub_Profile3d, f_transforms)},
    {"Lub_Profile3d", "sensor_hits", offsetof(VmLub_Profile3d, f_sensor_hits)},
    {"Lub_Profile3d", "joint_events",
     offsetof(VmLub_Profile3d, f_joint_events)},
    {"Lub_Profile3d", "hit_events", offsetof(VmLub_Profile3d, f_hit_events)},
    {"Lub_Profile3d", "refit", offsetof(VmLub_Profile3d, f_refit)},
    {"Lub_Profile3d", "bullets", offsetof(VmLub_Profile3d, f_bullets)},
    {"Lub_Profile3d", "sleep_islands",
     offsetof(VmLub_Profile3d, f_sleep_islands)},
    {"Lub_Profile3d", "sensors", offsetof(VmLub_Profile3d, f_sensors)},
    {"Lub_Counters3d", "body_count", offsetof(VmLub_Counters3d, f_body_count)},
    {"Lub_Counters3d", "shape_count",
     offsetof(VmLub_Counters3d, f_shape_count)},
    {"Lub_Counters3d", "contact_count",
     offsetof(VmLub_Counters3d, f_contact_count)},
    {"Lub_Counters3d", "joint_count",
     offsetof(VmLub_Counters3d, f_joint_count)},
    {"Lub_Counters3d", "island_count",
     offsetof(VmLub_Counters3d, f_island_count)},
    {"Lub_Counters3d", "stack_used", offsetof(VmLub_Counters3d, f_stack_used)},
    {"Lub_Counters3d", "arena_capacity",
     offsetof(VmLub_Counters3d, f_arena_capacity)},
    {"Lub_Counters3d", "static_tree_height",
     offsetof(VmLub_Counters3d, f_static_tree_height)},
    {"Lub_Counters3d", "tree_height",
     offsetof(VmLub_Counters3d, f_tree_height)},
    {"Lub_Counters3d", "sat_call_count",
     offsetof(VmLub_Counters3d, f_sat_call_count)},
    {"Lub_Counters3d", "sat_cache_hit_count",
     offsetof(VmLub_Counters3d, f_sat_cache_hit_count)},
    {"Lub_Counters3d", "byte_count", offsetof(VmLub_Counters3d, f_byte_count)},
    {"Lub_Counters3d", "task_count", offsetof(VmLub_Counters3d, f_task_count)},
    {"Lub_Counters3d", "awake_contact_count",
     offsetof(VmLub_Counters3d, f_awake_contact_count)},
    {"Lub_Counters3d", "recycled_contact_count",
     offsetof(VmLub_Counters3d, f_recycled_contact_count)},
    {"Lub_Counters3d", "distance_iterations",
     offsetof(VmLub_Counters3d, f_distance_iterations)},
    {"Lub_Counters3d", "push_back_iterations",
     offsetof(VmLub_Counters3d, f_push_back_iterations)},
    {"Lub_Counters3d", "root_iterations",
     offsetof(VmLub_Counters3d, f_root_iterations)},
    {"Lub_Counters3d", "color_counts",
     offsetof(VmLub_Counters3d, f_color_counts)},
    {"Lub_Counters3d", "manifold_counts",
     offsetof(VmLub_Counters3d, f_manifold_counts)},
    {"Lub_EventData", "kind", offsetof(VmLub_EventData, f_kind)},
    {"Lub_EventData", "key", offsetof(VmLub_EventData, f_key)},
    {"Lub_EventData", "button", offsetof(VmLub_EventData, f_button)},
    {"Lub_EventData", "x", offsetof(VmLub_EventData, f_x)},
    {"Lub_EventData", "y", offsetof(VmLub_EventData, f_y)},
    {"Lub_EventData", "dx", offsetof(VmLub_EventData, f_dx)},
    {"Lub_EventData", "dy", offsetof(VmLub_EventData, f_dy)},
    {NULL, NULL, 0},
};
static int vm_lub_verify(TvmVm *vm, char *error, size_t size) {
  for (int i = 0; vm_lub_layout[i].type; i++) {
    const TvmTypeInfo *t = tvm_type(vm, vm_lub_layout[i].type);
    if (!t)
      continue;
    int32_t at = tvm_type_field_offset(t, vm_lub_layout[i].field);
    if (at != (int32_t)vm_lub_layout[i].offset) {
      snprintf(error, size, "%s.%s is at %d in the VM, %d in the host",
               vm_lub_layout[i].type, vm_lub_layout[i].field, (int)at,
               (int)vm_lub_layout[i].offset);
      return 0;
    }
  }
  return 1;
}
static VmLubType vm_lub_types[102] = {
    {"Lub_TextureRef", 0, NULL},
    {"Lub_ShaderRef", 0, NULL},
    {"Lub_BufferRef", 0, NULL},
    {"Lub_FrameRange", 0, NULL},
    {"Lub_Bytes", 0, NULL},
    {"Lub_WorldRef", 0, NULL},
    {"Lub_BodyRef", 0, NULL},
    {"Lub_ShapeRef", 0, NULL},
    {"Lub_ChainRef", 0, NULL},
    {"Lub_JointRef", 0, NULL},
    {"Lub_WorldRef3d", 0, NULL},
    {"Lub_BodyRef3d", 0, NULL},
    {"Lub_ShapeRef3d", 0, NULL},
    {"Lub_JointRef3d", 0, NULL},
    {"Lub_XrView", 0, NULL},
    {"float[]", 0, NULL},
    {"Lub_XrInput", 0, NULL},
    {"Lub_GltfMesh", 0, NULL},
    {"List<float>", 0, NULL},
    {"List<int>", 0, NULL},
    {"Lub_SdfBone", 0, NULL},
    {"Lub_GltfPrimitive", 0, NULL},
    {"Lub_GltfMaterial", 0, NULL},
    {"Lub_MeshData", 0, NULL},
    {"Lub_FontMetrics", 0, NULL},
    {"Lub_GlyphBitmap", 0, NULL},
    {"Lub_GlyphMesh", 0, NULL},
    {"Lub_AudioInfo", 0, NULL},
    {"Lub_ShapeView", 0, NULL},
    {"Lub_PreSolveContact", 0, NULL},
    {"Lub_ManifoldPoint", 0, NULL},
    {"Lub_MaterialView", 0, NULL},
    {"Lub_WorldInfo", 0, NULL},
    {"Lub_WorldCallbackInfo", 0, NULL},
    {"Lub_Vec2d", 0, NULL},
    {"Lub_JointInfo", 0, NULL},
    {"Lub_StepInfo", 0, NULL},
    {"Lub_Pose", 0, NULL},
    {"Lub_Velocity", 0, NULL},
    {"Lub_MassData", 0, NULL},
    {"Lub_JointView", 0, NULL},
    {"Lub_ContactData", 0, NULL},
    {"Lub_ShapeRayHit", 0, NULL},
    {"Lub_Aabb", 0, NULL},
    {"Lub_ShapeInfo", 0, NULL},
    {"Lub_FilterInfo", 0, NULL},
    {"Lub_ContactEvent", 0, NULL},
    {"Lub_BodyEvent", 0, NULL},
    {"Lub_SensorEvent", 0, NULL},
    {"Lub_RayHit", 0, NULL},
    {"Lub_MoverCast", 0, NULL},
    {"Lub_MoverPlane", 0, NULL},
    {"Lub_DebugData", 0, NULL},
    {"Lub_Profile", 0, NULL},
    {"Lub_Counters", 0, NULL},
    {"Lub_ShapeView3d", 0, NULL},
    {"Lub_PreSolveContact3d", 0, NULL},
    {"Lub_WorldInfo3d", 0, NULL},
    {"Lub_Vec3d", 0, NULL},
    {"Lub_JointInfo3d", 0, NULL},
    {"Lub_Frame3d", 0, NULL},
    {"Lub_JointView3d", 0, NULL},
    {"Lub_MoverCast3d", 0, NULL},
    {"Lub_MoverPlane3d", 0, NULL},
    {"Lub_StepInfo3d", 0, NULL},
    {"Lub_Pose3d", 0, NULL},
    {"Lub_Velocity3d", 0, NULL},
    {"Lub_MassData3d", 0, NULL},
    {"Lub_Inertia3d", 0, NULL},
    {"Lub_ContactEvent3d", 0, NULL},
    {"Lub_BodyEvent3d", 0, NULL},
    {"Lub_SensorEvent3d", 0, NULL},
    {"Lub_JointEvent3d", 0, NULL},
    {"Lub_RayHit3d", 0, NULL},
    {"Lub_ContactData3d", 0, NULL},
    {"Lub_ShapeRayHit3d", 0, NULL},
    {"Lub_Aabb3d", 0, NULL},
    {"Lub_ShapeInfo3d", 0, NULL},
    {"Lub_Profile3d", 0, NULL},
    {"Lub_Counters3d", 0, NULL},
    {"Lub_EventData", 0, NULL},
    {"List<Lub_SdfBone>", 0, NULL},
    {"List<Lub_GltfPrimitive>", 0, NULL},
    {"List<Lub_ManifoldPoint>", 0, NULL},
    {"List<Lub_ShapeView>", 0, NULL},
    {"List<Lub_JointView>", 0, NULL},
    {"List<Lub_ContactData>", 0, NULL},
    {"List<Lub_ContactEvent>", 0, NULL},
    {"List<Lub_BodyEvent>", 0, NULL},
    {"List<Lub_SensorEvent>", 0, NULL},
    {"List<Lub_RayHit>", 0, NULL},
    {"List<Lub_MoverPlane>", 0, NULL},
    {"List<Lub_JointView3d>", 0, NULL},
    {"List<Lub_MoverPlane3d>", 0, NULL},
    {"List<Lub_ContactEvent3d>", 0, NULL},
    {"List<Lub_BodyEvent3d>", 0, NULL},
    {"List<Lub_SensorEvent3d>", 0, NULL},
    {"List<Lub_JointEvent3d>", 0, NULL},
    {"List<Lub_RayHit3d>", 0, NULL},
    {"List<Lub_ShapeView3d>", 0, NULL},
    {"List<Lub_ContactData3d>", 0, NULL},
    {"Lub_Readback", 0, NULL},
};
static LubView *vm_lub_views;
static uint32_t vm_lub_view_count, vm_lub_view_capacity;
static int32_t vm_lub_view_frame;
static uint64_t vm_lub_view_store(LubView value) {
  int32_t frame = lub_frame_index(vm_lub_context);
  if (frame != vm_lub_view_frame) {
    vm_lub_view_frame = frame;
    vm_lub_view_count = 0;
  }
  if (vm_lub_view_count == vm_lub_view_capacity) {
    vm_lub_view_capacity = vm_lub_view_capacity ? vm_lub_view_capacity * 2 : 16;
    vm_lub_views =
        realloc(vm_lub_views, vm_lub_view_capacity * sizeof(*vm_lub_views));
    if (!vm_lub_views)
      abort();
  }
  vm_lub_views[vm_lub_view_count] = value;
  return (uint64_t)(uint32_t)frame << 32 | vm_lub_view_count++;
}
static LubView vm_lub_view_load(uint64_t host_value) {
  int32_t frame = lub_frame_index(vm_lub_context);
  uint32_t index = (uint32_t)host_value;
  if ((int32_t)(host_value >> 32) != frame || frame != vm_lub_view_frame ||
      index >= vm_lub_view_count || vm_lub_views[index].frame != frame) {
    vm_lub_fail("lub-expired-view");
    return (LubView){0};
  }
  return vm_lub_views[index];
}
#include <inttypes.h>
static void vm_lub_bits_fault(const char *what, LubStr text) {
  if (vm_lub_error)
    return;
  snprintf(vm_lub_error_text, sizeof vm_lub_error_text, "bit mask: %s '%.*s'",
           what, (int)text.len, text.ptr);
  vm_lub_error = vm_lub_error_text;
}
static bool vm_lub_bits(void *string, uint64_t *out) {
  if (!string)
    return false;
  LubStr text = vm_lub_str(string);
  int32_t length = text.len, i = 0;
  if (length >= 2 && text.ptr[0] == '0' &&
      (text.ptr[1] == 'x' || text.ptr[1] == 'X'))
    i = 2;
  if (i == length) {
    vm_lub_bits_fault("empty hex string", text);
    return false;
  }
  if (length - i > 16) {
    vm_lub_bits_fault("more than 16 hex digits", text);
    return false;
  }
  uint64_t value = 0;
  for (; i < length; i++) {
    unsigned char c = (unsigned char)text.ptr[i];
    int digit = c >= '0' && c <= '9'   ? c - '0'
                : c >= 'a' && c <= 'f' ? c - 'a' + 10
                : c >= 'A' && c <= 'F' ? c - 'A' + 10
                                       : -1;
    if (digit < 0) {
      vm_lub_bits_fault("invalid hex", text);
      return false;
    }
    value = value << 4 | (uint64_t)digit;
  }
  *out = value;
  return true;
}
static void *vm_lub_bits_string(uint64_t value) {
  char text[17];
  snprintf(text, sizeof(text), "%016" PRIx64, value);
  return tvm_string_new(vm_lub_vm, text, 16);
}
static VmLub_TextureRef *vm_lub_handle_TextureRef(LubHandle handle) {
  VmLub_TextureRef *result = vm_lub_new(&vm_lub_types[0], sizeof(*result));
  result->host_value = (uint32_t)handle;
  if (handle) {
    LubStr key;
    int32_t version = 0;
    lub_gfx_resource_info(vm_lub_context, handle, &key, &version);
    result->f_version = version;
  }
  return result;
}
static VmLub_ShaderRef *vm_lub_handle_ShaderRef(LubHandle handle) {
  VmLub_ShaderRef *result = vm_lub_new(&vm_lub_types[1], sizeof(*result));
  result->host_value = (uint32_t)handle;
  if (handle) {
    LubStr key;
    int32_t version = 0;
    lub_gfx_resource_info(vm_lub_context, handle, &key, &version);
    result->f_version = version;
  }
  return result;
}
static VmLub_BufferRef *vm_lub_handle_BufferRef(LubHandle handle) {
  VmLub_BufferRef *result = vm_lub_new(&vm_lub_types[2], sizeof(*result));
  result->host_value = (uint32_t)handle;
  if (handle) {
    LubStr key;
    int32_t version = 0;
    lub_gfx_resource_info(vm_lub_context, handle, &key, &version);
    result->f_version = version;
  }
  return result;
}
static VmLub_FrameRange *vm_lub_handle_FrameRange(LubHandle handle) {
  VmLub_FrameRange *result = vm_lub_new(&vm_lub_types[3], sizeof(*result));
  result->host_value = (uint32_t)handle;
  return result;
}
static VmLub_Bytes *vm_lub_view_Bytes(LubView value) {
  if (!value.ptr)
    return NULL;
  VmLub_Bytes *result = vm_lub_new(&vm_lub_types[4], sizeof(*result));
  result->host_value = vm_lub_view_store(value);
  result->f_length = value.len;
  return result;
}
static VmLub_WorldRef *vm_lub_handle_WorldRef(LubHandle handle) {
  VmLub_WorldRef *result = vm_lub_new(&vm_lub_types[5], sizeof(*result));
  result->host_value = (uint32_t)handle;
  return result;
}
static VmLub_BodyRef *vm_lub_handle_BodyRef(LubHandle handle) {
  VmLub_BodyRef *result = vm_lub_new(&vm_lub_types[6], sizeof(*result));
  result->host_value = (uint32_t)handle;
  return result;
}
static VmLub_ShapeRef *vm_lub_handle_ShapeRef(LubHandle handle) {
  VmLub_ShapeRef *result = vm_lub_new(&vm_lub_types[7], sizeof(*result));
  result->host_value = (uint32_t)handle;
  return result;
}
static VmLub_ChainRef *vm_lub_handle_ChainRef(LubHandle handle) {
  VmLub_ChainRef *result = vm_lub_new(&vm_lub_types[8], sizeof(*result));
  result->host_value = (uint32_t)handle;
  return result;
}
static VmLub_JointRef *vm_lub_handle_JointRef(LubHandle handle) {
  VmLub_JointRef *result = vm_lub_new(&vm_lub_types[9], sizeof(*result));
  result->host_value = (uint32_t)handle;
  return result;
}
static VmLub_WorldRef3d *vm_lub_handle_WorldRef3d(LubHandle handle) {
  VmLub_WorldRef3d *result = vm_lub_new(&vm_lub_types[10], sizeof(*result));
  result->host_value = (uint32_t)handle;
  return result;
}
static VmLub_BodyRef3d *vm_lub_handle_BodyRef3d(LubHandle handle) {
  VmLub_BodyRef3d *result = vm_lub_new(&vm_lub_types[11], sizeof(*result));
  result->host_value = (uint32_t)handle;
  return result;
}
static VmLub_ShapeRef3d *vm_lub_handle_ShapeRef3d(LubHandle handle) {
  VmLub_ShapeRef3d *result = vm_lub_new(&vm_lub_types[12], sizeof(*result));
  result->host_value = (uint32_t)handle;
  return result;
}
static VmLub_JointRef3d *vm_lub_handle_JointRef3d(LubHandle handle) {
  VmLub_JointRef3d *result = vm_lub_new(&vm_lub_types[13], sizeof(*result));
  result->host_value = (uint32_t)handle;
  return result;
}
static LubConfigOpts vm_lub_to_ConfigOpts(VmLub_ConfigOpts *source);
static LubPassOpts vm_lub_to_PassOpts(VmLub_PassOpts *source);
static LubTextureOpts vm_lub_to_TextureOpts(VmLub_TextureOpts *source);
static LubDrawOpts vm_lub_to_DrawOpts(VmLub_DrawOpts *source);
static LubDispatchOpts vm_lub_to_DispatchOpts(VmLub_DispatchOpts *source);
static LubMeshData vm_lub_to_MeshData(VmLub_MeshData *source);
static LubSdfBone vm_lub_to_SdfBone(VmLub_SdfBone *source);
static LubSdfNodeDesc vm_lub_to_SdfNodeDesc(VmLub_SdfNodeDesc *source);
static LubPlayOpts vm_lub_to_PlayOpts(VmLub_PlayOpts *source);
static LubVoiceOpts vm_lub_to_VoiceOpts(VmLub_VoiceOpts *source);
static LubWorldOpts vm_lub_to_WorldOpts(VmLub_WorldOpts *source);
static LubVec2d vm_lub_to_Vec2d(VmLub_Vec2d *source);
static LubWorldCallbacks vm_lub_to_WorldCallbacks(VmLub_WorldCallbacks *source);
static LubBeginOpts vm_lub_to_BeginOpts(VmLub_BeginOpts *source);
static LubBodyDesc vm_lub_to_BodyDesc(VmLub_BodyDesc *source);
static LubInitialState vm_lub_to_InitialState(VmLub_InitialState *source);
static LubBoxDesc vm_lub_to_BoxDesc(VmLub_BoxDesc *source);
static LubFilterDesc vm_lub_to_FilterDesc(VmLub_FilterDesc *source);
static LubCircleDesc vm_lub_to_CircleDesc(VmLub_CircleDesc *source);
static LubCapsuleDesc vm_lub_to_CapsuleDesc(VmLub_CapsuleDesc *source);
static LubSegmentDesc vm_lub_to_SegmentDesc(VmLub_SegmentDesc *source);
static LubPolygonDesc vm_lub_to_PolygonDesc(VmLub_PolygonDesc *source);
static LubChainDesc vm_lub_to_ChainDesc(VmLub_ChainDesc *source);
static LubChainMaterial vm_lub_to_ChainMaterial(VmLub_ChainMaterial *source);
static LubJointDesc vm_lub_to_JointDesc(VmLub_JointDesc *source);
static LubJointSpringDesc
vm_lub_to_JointSpringDesc(VmLub_JointSpringDesc *source);
static LubJointLimitDesc vm_lub_to_JointLimitDesc(VmLub_JointLimitDesc *source);
static LubJointMotorDesc vm_lub_to_JointMotorDesc(VmLub_JointMotorDesc *source);
static LubJointTargetDesc
vm_lub_to_JointTargetDesc(VmLub_JointTargetDesc *source);
static LubRaycastDesc vm_lub_to_RaycastDesc(VmLub_RaycastDesc *source);
static LubMaterialDesc vm_lub_to_MaterialDesc(VmLub_MaterialDesc *source);
static LubShapeEventsDesc
vm_lub_to_ShapeEventsDesc(VmLub_ShapeEventsDesc *source);
static LubAabbDesc vm_lub_to_AabbDesc(VmLub_AabbDesc *source);
static LubShapeCastDesc vm_lub_to_ShapeCastDesc(VmLub_ShapeCastDesc *source);
static LubMoverDesc vm_lub_to_MoverDesc(VmLub_MoverDesc *source);
static LubExplosionDesc vm_lub_to_ExplosionDesc(VmLub_ExplosionDesc *source);
static LubDebugOpts vm_lub_to_DebugOpts(VmLub_DebugOpts *source);
static LubCommandOpts vm_lub_to_CommandOpts(VmLub_CommandOpts *source);
static LubVelocityDesc vm_lub_to_VelocityDesc(VmLub_VelocityDesc *source);
static LubPoseDesc vm_lub_to_PoseDesc(VmLub_PoseDesc *source);
static LubMassDataDesc vm_lub_to_MassDataDesc(VmLub_MassDataDesc *source);
static LubWorldOpts3d vm_lub_to_WorldOpts3d(VmLub_WorldOpts3d *source);
static LubVec3d vm_lub_to_Vec3d(VmLub_Vec3d *source);
static LubWorldCallbacks3d
vm_lub_to_WorldCallbacks3d(VmLub_WorldCallbacks3d *source);
static LubBeginOpts3d vm_lub_to_BeginOpts3d(VmLub_BeginOpts3d *source);
static LubBodyDesc3d vm_lub_to_BodyDesc3d(VmLub_BodyDesc3d *source);
static LubMotionLocks3d vm_lub_to_MotionLocks3d(VmLub_MotionLocks3d *source);
static LubInitialState3d vm_lub_to_InitialState3d(VmLub_InitialState3d *source);
static LubQuat3d vm_lub_to_Quat3d(VmLub_Quat3d *source);
static LubSphereDesc3d vm_lub_to_SphereDesc3d(VmLub_SphereDesc3d *source);
static LubFilterDesc3d vm_lub_to_FilterDesc3d(VmLub_FilterDesc3d *source);
static LubBoxDesc3d vm_lub_to_BoxDesc3d(VmLub_BoxDesc3d *source);
static LubCapsuleDesc3d vm_lub_to_CapsuleDesc3d(VmLub_CapsuleDesc3d *source);
static LubCylinderDesc3d vm_lub_to_CylinderDesc3d(VmLub_CylinderDesc3d *source);
static LubConeDesc3d vm_lub_to_ConeDesc3d(VmLub_ConeDesc3d *source);
static LubHullDesc3d vm_lub_to_HullDesc3d(VmLub_HullDesc3d *source);
static LubMeshDesc3d vm_lub_to_MeshDesc3d(VmLub_MeshDesc3d *source);
static LubSurfaceMaterial3d
vm_lub_to_SurfaceMaterial3d(VmLub_SurfaceMaterial3d *source);
static LubHeightFieldDesc3d
vm_lub_to_HeightFieldDesc3d(VmLub_HeightFieldDesc3d *source);
static LubCompoundDesc3d vm_lub_to_CompoundDesc3d(VmLub_CompoundDesc3d *source);
static LubCompoundChild3d
vm_lub_to_CompoundChild3d(VmLub_CompoundChild3d *source);
static LubFrameDesc3d vm_lub_to_FrameDesc3d(VmLub_FrameDesc3d *source);
static LubCompoundSphere3d
vm_lub_to_CompoundSphere3d(VmLub_CompoundSphere3d *source);
static LubCompoundBox3d vm_lub_to_CompoundBox3d(VmLub_CompoundBox3d *source);
static LubCompoundCapsule3d
vm_lub_to_CompoundCapsule3d(VmLub_CompoundCapsule3d *source);
static LubJointDesc3d vm_lub_to_JointDesc3d(VmLub_JointDesc3d *source);
static LubJointSpringDesc3d
vm_lub_to_JointSpringDesc3d(VmLub_JointSpringDesc3d *source);
static LubJointLimitDesc3d
vm_lub_to_JointLimitDesc3d(VmLub_JointLimitDesc3d *source);
static LubJointMotorDesc3d
vm_lub_to_JointMotorDesc3d(VmLub_JointMotorDesc3d *source);
static LubJointTargetDesc3d
vm_lub_to_JointTargetDesc3d(VmLub_JointTargetDesc3d *source);
static LubMoverDesc3d vm_lub_to_MoverDesc3d(VmLub_MoverDesc3d *source);
static LubCommandOpts3d vm_lub_to_CommandOpts3d(VmLub_CommandOpts3d *source);
static LubVelocityDesc3d vm_lub_to_VelocityDesc3d(VmLub_VelocityDesc3d *source);
static LubPoseDesc3d vm_lub_to_PoseDesc3d(VmLub_PoseDesc3d *source);
static LubTargetDesc3d vm_lub_to_TargetDesc3d(VmLub_TargetDesc3d *source);
static LubRaycastDesc3d vm_lub_to_RaycastDesc3d(VmLub_RaycastDesc3d *source);
static LubAabbDesc3d vm_lub_to_AabbDesc3d(VmLub_AabbDesc3d *source);
static LubShapeProxyDesc3d
vm_lub_to_ShapeProxyDesc3d(VmLub_ShapeProxyDesc3d *source);
static LubSphereProxy3d vm_lub_to_SphereProxy3d(VmLub_SphereProxy3d *source);
static LubBoxProxy3d vm_lub_to_BoxProxy3d(VmLub_BoxProxy3d *source);
static LubCapsuleProxy3d vm_lub_to_CapsuleProxy3d(VmLub_CapsuleProxy3d *source);
static LubMaterialDesc3d vm_lub_to_MaterialDesc3d(VmLub_MaterialDesc3d *source);
static LubShapeEventsDesc3d
vm_lub_to_ShapeEventsDesc3d(VmLub_ShapeEventsDesc3d *source);
VM_LUB_UNUSED static VmLub_XrView *vm_lub_from_XrView(const LubXrView *source);
VM_LUB_UNUSED static VmLub_XrInput *
vm_lub_from_XrInput(const LubXrInput *source);
VM_LUB_UNUSED static VmLub_GltfMesh *
vm_lub_from_GltfMesh(const LubGltfMesh *source);
VM_LUB_UNUSED static VmLub_SdfBone *
vm_lub_from_SdfBone(const LubSdfBone *source);
VM_LUB_UNUSED static VmLub_GltfPrimitive *
vm_lub_from_GltfPrimitive(const LubGltfPrimitive *source);
VM_LUB_UNUSED static VmLub_GltfMaterial *
vm_lub_from_GltfMaterial(const LubGltfMaterial *source);
VM_LUB_UNUSED static VmLub_MeshData *
vm_lub_from_MeshData(const LubMeshData *source);
VM_LUB_UNUSED static VmLub_FontMetrics *
vm_lub_from_FontMetrics(const LubFontMetrics *source);
VM_LUB_UNUSED static VmLub_GlyphBitmap *
vm_lub_from_GlyphBitmap(const LubGlyphBitmap *source);
VM_LUB_UNUSED static VmLub_GlyphMesh *
vm_lub_from_GlyphMesh(const LubGlyphMesh *source);
VM_LUB_UNUSED static VmLub_AudioInfo *
vm_lub_from_AudioInfo(const LubAudioInfo *source);
VM_LUB_UNUSED static VmLub_ShapeView *
vm_lub_from_ShapeView(const LubShapeView *source);
VM_LUB_UNUSED static VmLub_PreSolveContact *
vm_lub_from_PreSolveContact(const LubPreSolveContact *source);
VM_LUB_UNUSED static VmLub_ManifoldPoint *
vm_lub_from_ManifoldPoint(const LubManifoldPoint *source);
VM_LUB_UNUSED static VmLub_MaterialView *
vm_lub_from_MaterialView(const LubMaterialView *source);
VM_LUB_UNUSED static VmLub_WorldInfo *
vm_lub_from_WorldInfo(const LubWorldInfo *source);
VM_LUB_UNUSED static VmLub_WorldCallbackInfo *
vm_lub_from_WorldCallbackInfo(const LubWorldCallbackInfo *source);
VM_LUB_UNUSED static VmLub_Vec2d *vm_lub_from_Vec2d(const LubVec2d *source);
VM_LUB_UNUSED static VmLub_JointInfo *
vm_lub_from_JointInfo(const LubJointInfo *source);
VM_LUB_UNUSED static VmLub_StepInfo *
vm_lub_from_StepInfo(const LubStepInfo *source);
VM_LUB_UNUSED static VmLub_Pose *vm_lub_from_Pose(const LubPose *source);
VM_LUB_UNUSED static VmLub_Velocity *
vm_lub_from_Velocity(const LubVelocity *source);
VM_LUB_UNUSED static VmLub_MassData *
vm_lub_from_MassData(const LubMassData *source);
VM_LUB_UNUSED static VmLub_JointView *
vm_lub_from_JointView(const LubJointView *source);
VM_LUB_UNUSED static VmLub_ContactData *
vm_lub_from_ContactData(const LubContactData *source);
VM_LUB_UNUSED static VmLub_ShapeRayHit *
vm_lub_from_ShapeRayHit(const LubShapeRayHit *source);
VM_LUB_UNUSED static VmLub_Aabb *vm_lub_from_Aabb(const LubAabb *source);
VM_LUB_UNUSED static VmLub_ShapeInfo *
vm_lub_from_ShapeInfo(const LubShapeInfo *source);
VM_LUB_UNUSED static VmLub_FilterInfo *
vm_lub_from_FilterInfo(const LubFilterInfo *source);
VM_LUB_UNUSED static VmLub_ContactEvent *
vm_lub_from_ContactEvent(const LubContactEvent *source);
VM_LUB_UNUSED static VmLub_BodyEvent *
vm_lub_from_BodyEvent(const LubBodyEvent *source);
VM_LUB_UNUSED static VmLub_SensorEvent *
vm_lub_from_SensorEvent(const LubSensorEvent *source);
VM_LUB_UNUSED static VmLub_RayHit *vm_lub_from_RayHit(const LubRayHit *source);
VM_LUB_UNUSED static VmLub_MoverCast *
vm_lub_from_MoverCast(const LubMoverCast *source);
VM_LUB_UNUSED static VmLub_MoverPlane *
vm_lub_from_MoverPlane(const LubMoverPlane *source);
VM_LUB_UNUSED static VmLub_DebugData *
vm_lub_from_DebugData(const LubDebugData *source);
VM_LUB_UNUSED static VmLub_Profile *
vm_lub_from_Profile(const LubProfile *source);
VM_LUB_UNUSED static VmLub_Counters *
vm_lub_from_Counters(const LubCounters *source);
VM_LUB_UNUSED static VmLub_ShapeView3d *
vm_lub_from_ShapeView3d(const LubShapeView3d *source);
VM_LUB_UNUSED static VmLub_PreSolveContact3d *
vm_lub_from_PreSolveContact3d(const LubPreSolveContact3d *source);
VM_LUB_UNUSED static VmLub_WorldInfo3d *
vm_lub_from_WorldInfo3d(const LubWorldInfo3d *source);
VM_LUB_UNUSED static VmLub_Vec3d *vm_lub_from_Vec3d(const LubVec3d *source);
VM_LUB_UNUSED static VmLub_JointInfo3d *
vm_lub_from_JointInfo3d(const LubJointInfo3d *source);
VM_LUB_UNUSED static VmLub_Frame3d *
vm_lub_from_Frame3d(const LubFrame3d *source);
VM_LUB_UNUSED static VmLub_JointView3d *
vm_lub_from_JointView3d(const LubJointView3d *source);
VM_LUB_UNUSED static VmLub_MoverCast3d *
vm_lub_from_MoverCast3d(const LubMoverCast3d *source);
VM_LUB_UNUSED static VmLub_MoverPlane3d *
vm_lub_from_MoverPlane3d(const LubMoverPlane3d *source);
VM_LUB_UNUSED static VmLub_StepInfo3d *
vm_lub_from_StepInfo3d(const LubStepInfo3d *source);
VM_LUB_UNUSED static VmLub_Pose3d *vm_lub_from_Pose3d(const LubPose3d *source);
VM_LUB_UNUSED static VmLub_Velocity3d *
vm_lub_from_Velocity3d(const LubVelocity3d *source);
VM_LUB_UNUSED static VmLub_MassData3d *
vm_lub_from_MassData3d(const LubMassData3d *source);
VM_LUB_UNUSED static VmLub_Inertia3d *
vm_lub_from_Inertia3d(const LubInertia3d *source);
VM_LUB_UNUSED static VmLub_ContactEvent3d *
vm_lub_from_ContactEvent3d(const LubContactEvent3d *source);
VM_LUB_UNUSED static VmLub_BodyEvent3d *
vm_lub_from_BodyEvent3d(const LubBodyEvent3d *source);
VM_LUB_UNUSED static VmLub_SensorEvent3d *
vm_lub_from_SensorEvent3d(const LubSensorEvent3d *source);
VM_LUB_UNUSED static VmLub_JointEvent3d *
vm_lub_from_JointEvent3d(const LubJointEvent3d *source);
VM_LUB_UNUSED static VmLub_RayHit3d *
vm_lub_from_RayHit3d(const LubRayHit3d *source);
VM_LUB_UNUSED static VmLub_ContactData3d *
vm_lub_from_ContactData3d(const LubContactData3d *source);
VM_LUB_UNUSED static VmLub_ShapeRayHit3d *
vm_lub_from_ShapeRayHit3d(const LubShapeRayHit3d *source);
VM_LUB_UNUSED static VmLub_Aabb3d *vm_lub_from_Aabb3d(const LubAabb3d *source);
VM_LUB_UNUSED static VmLub_ShapeInfo3d *
vm_lub_from_ShapeInfo3d(const LubShapeInfo3d *source);
VM_LUB_UNUSED static VmLub_Profile3d *
vm_lub_from_Profile3d(const LubProfile3d *source);
VM_LUB_UNUSED static VmLub_Counters3d *
vm_lub_from_Counters3d(const LubCounters3d *source);
VM_LUB_UNUSED static VmLub_EventData *
vm_lub_from_EventData(const LubEventData *source);
static LubSdfBone *vm_lub_to_SdfBone_list(void *source, int32_t *count);
static LubSdfNodeDesc *vm_lub_to_SdfNodeDesc_list(void *source, int32_t *count);
static LubChainMaterial *vm_lub_to_ChainMaterial_list(void *source,
                                                      int32_t *count);
static LubSurfaceMaterial3d *vm_lub_to_SurfaceMaterial3d_list(void *source,
                                                              int32_t *count);
static LubCompoundChild3d *vm_lub_to_CompoundChild3d_list(void *source,
                                                          int32_t *count);
static void *vm_lub_from_SdfBone_list(void *list, const LubSdfBone *source,
                                      int32_t count);
static void *vm_lub_from_GltfPrimitive_list(void *list,
                                            const LubGltfPrimitive *source,
                                            int32_t count);
static void *vm_lub_from_ManifoldPoint_list(void *list,
                                            const LubManifoldPoint *source,
                                            int32_t count);
static void *vm_lub_from_ShapeView_list(void *list, const LubShapeView *source,
                                        int32_t count);
static void *vm_lub_from_JointView_list(void *list, const LubJointView *source,
                                        int32_t count);
static void *vm_lub_from_ContactData_list(void *list,
                                          const LubContactData *source,
                                          int32_t count);
static void *vm_lub_from_ContactEvent_list(void *list,
                                           const LubContactEvent *source,
                                           int32_t count);
static void *vm_lub_from_BodyEvent_list(void *list, const LubBodyEvent *source,
                                        int32_t count);
static void *vm_lub_from_SensorEvent_list(void *list,
                                          const LubSensorEvent *source,
                                          int32_t count);
static void *vm_lub_from_RayHit_list(void *list, const LubRayHit *source,
                                     int32_t count);
static void *vm_lub_from_MoverPlane_list(void *list,
                                         const LubMoverPlane *source,
                                         int32_t count);
static void *vm_lub_from_JointView3d_list(void *list,
                                          const LubJointView3d *source,
                                          int32_t count);
static void *vm_lub_from_MoverPlane3d_list(void *list,
                                           const LubMoverPlane3d *source,
                                           int32_t count);
static void *vm_lub_from_ContactEvent3d_list(void *list,
                                             const LubContactEvent3d *source,
                                             int32_t count);
static void *vm_lub_from_BodyEvent3d_list(void *list,
                                          const LubBodyEvent3d *source,
                                          int32_t count);
static void *vm_lub_from_SensorEvent3d_list(void *list,
                                            const LubSensorEvent3d *source,
                                            int32_t count);
static void *vm_lub_from_JointEvent3d_list(void *list,
                                           const LubJointEvent3d *source,
                                           int32_t count);
static void *vm_lub_from_RayHit3d_list(void *list, const LubRayHit3d *source,
                                       int32_t count);
static void *vm_lub_from_ShapeView3d_list(void *list,
                                          const LubShapeView3d *source,
                                          int32_t count);
static void *vm_lub_from_ContactData3d_list(void *list,
                                            const LubContactData3d *source,
                                            int32_t count);
static LubConfigOpts vm_lub_to_ConfigOpts(VmLub_ConfigOpts *source) {
  LubConfigOpts result = {0};
  if (!source)
    return result;
  result.backend = vm_lub_str(source->f_backend);
  if (source->f_width.has) {
    result.has_width = true;
    result.width = source->f_width.v;
  }
  if (source->f_height.has) {
    result.has_height = true;
    result.height = source->f_height.v;
  }
  if (source->f_resource_sweep_after_frames.has) {
    result.has_resource_sweep_after_frames = true;
    result.resource_sweep_after_frames =
        source->f_resource_sweep_after_frames.v;
  }
  if (source->f_readback_depth.has) {
    result.has_readback_depth = true;
    result.readback_depth = source->f_readback_depth.v;
  }
  if (source->f_readback_fixed_latency.has) {
    result.has_readback_fixed_latency = true;
    result.readback_fixed_latency = source->f_readback_fixed_latency.v;
  }
  return result;
}
static LubPassOpts vm_lub_to_PassOpts(VmLub_PassOpts *source) {
  LubPassOpts result = {0};
  if (!source)
    return result;
  result.target =
      (source->f_target ? (int32_t)source->f_target->host_value : 0);
  if (source->f_targets) {
    result.targets_count = tvm_list_count(source->f_targets);
    LubHandle *items =
        vm_lub_alloc((size_t)result.targets_count * sizeof(*items));
    for (int i = 0; i < result.targets_count; i++) {
      VmLub_TextureRef *item =
          ((VmLub_TextureRef **)tvm_list_data(source->f_targets))[i];
      items[i] = item ? (int32_t)item->host_value : 0;
    }
    result.targets = items;
  }
  result.depth_target =
      (source->f_depth_target ? (int32_t)source->f_depth_target->host_value
                              : 0);
  if (source->f_clear_color) {
    result.has_clear_color = true;
    if (tvm_array_len(source->f_clear_color) != 4)
      vm_lub_fail("lub-array-length");
    else
      memcpy(result.clear_color, tvm_array_data(source->f_clear_color),
             sizeof(result.clear_color));
  }
  if (source->f_clear_colors) {
    result.clear_colors_count = tvm_list_count(source->f_clear_colors);
    float(*items)[4] =
        vm_lub_alloc((size_t)result.clear_colors_count * sizeof(*items));
    for (int i = 0; i < result.clear_colors_count; i++) {
      void *item = ((void **)tvm_list_data(source->f_clear_colors))[i];
      if (tvm_array_len(item) != 4)
        vm_lub_fail("lub-array-length");
      else
        memcpy(items[i], tvm_array_data(item), sizeof(items[i]));
    }
    result.clear_colors = items;
  }
  if (source->f_clear_depth.has) {
    result.has_clear_depth = true;
    result.clear_depth = source->f_clear_depth.v;
  }
  if (source->f_load.has) {
    result.has_load = true;
    result.load = source->f_load.v;
  }
  return result;
}
static LubTextureOpts vm_lub_to_TextureOpts(VmLub_TextureOpts *source) {
  LubTextureOpts result = {0};
  if (!source)
    return result;
  if (source->f_filter.has) {
    result.has_filter = true;
    result.filter = source->f_filter.v;
  }
  if (source->f_wrap.has) {
    result.has_wrap = true;
    result.wrap = source->f_wrap.v;
  }
  if (source->f_target.has) {
    result.has_target = true;
    result.target = source->f_target.v;
  }
  if (source->f_storage.has) {
    result.has_storage = true;
    result.storage = source->f_storage.v;
  }
  return result;
}
static LubDrawOpts vm_lub_to_DrawOpts(VmLub_DrawOpts *source) {
  LubDrawOpts result = {0};
  if (!source)
    return result;
  result.shader =
      (source->f_shader ? (int32_t)source->f_shader->host_value : 0);
  if (source->f_blend.has) {
    result.has_blend = true;
    result.blend = source->f_blend.v;
  }
  if (source->f_cull.has) {
    result.has_cull = true;
    result.cull = source->f_cull.v;
  }
  if (source->f_primitive.has) {
    result.has_primitive = true;
    result.primitive = source->f_primitive.v;
  }
  if (source->f_depth.has) {
    result.has_depth = true;
    result.depth = source->f_depth.v;
  }
  if (source->f_depth_write.has) {
    result.has_depth_write = true;
    result.depth_write = source->f_depth_write.v;
  }
  return result;
}
static LubDispatchOpts vm_lub_to_DispatchOpts(VmLub_DispatchOpts *source) {
  LubDispatchOpts result = {0};
  if (!source)
    return result;
  result.shader =
      (source->f_shader ? (int32_t)source->f_shader->host_value : 0);
  return result;
}
static LubMeshData vm_lub_to_MeshData(VmLub_MeshData *source) {
  LubMeshData result = {0};
  if (!source)
    return result;
  if (source->f_positions) {
    result.positions_count = tvm_list_count(source->f_positions);
    result.positions = tvm_list_data(source->f_positions);
  }
  if (source->f_normals) {
    result.normals_count = tvm_list_count(source->f_normals);
    result.normals = tvm_list_data(source->f_normals);
  }
  if (source->f_indices) {
    result.indices_count = tvm_list_count(source->f_indices);
    result.indices = tvm_list_data(source->f_indices);
  }
  result.vert_count = source->f_vert_count;
  result.index_count = source->f_index_count;
  if (source->f_uvs) {
    result.uvs_count = tvm_list_count(source->f_uvs);
    result.uvs = tvm_list_data(source->f_uvs);
  }
  if (source->f_tangents) {
    result.tangents_count = tvm_list_count(source->f_tangents);
    result.tangents = tvm_list_data(source->f_tangents);
  }
  if (source->f_bounds_min) {
    result.bounds_min_count = tvm_list_count(source->f_bounds_min);
    result.bounds_min = tvm_list_data(source->f_bounds_min);
  }
  if (source->f_bounds_max) {
    result.bounds_max_count = tvm_list_count(source->f_bounds_max);
    result.bounds_max = tvm_list_data(source->f_bounds_max);
  }
  if (source->f_cell.has) {
    result.has_cell = true;
    result.cell = source->f_cell.v;
  }
  if (source->f_colors) {
    result.colors_count = tvm_list_count(source->f_colors);
    result.colors = tvm_list_data(source->f_colors);
  }
  if (source->f_metal_rough) {
    result.metal_rough_count = tvm_list_count(source->f_metal_rough);
    result.metal_rough = tvm_list_data(source->f_metal_rough);
  }
  if (source->f_joints) {
    result.joints_count = tvm_list_count(source->f_joints);
    result.joints = tvm_list_data(source->f_joints);
  }
  if (source->f_weights) {
    result.weights_count = tvm_list_count(source->f_weights);
    result.weights = tvm_list_data(source->f_weights);
  }
  if (source->f_bones) {
    result.bones = vm_lub_to_SdfBone_list(source->f_bones, &result.bones_count);
  }
  return result;
}
static LubSdfBone vm_lub_to_SdfBone(VmLub_SdfBone *source) {
  LubSdfBone result = {0};
  if (!source)
    return result;
  result.name = vm_lub_str(source->f_name);
  result.x = source->f_x;
  result.y = source->f_y;
  result.z = source->f_z;
  return result;
}
static LubSdfNodeDesc vm_lub_to_SdfNodeDesc(VmLub_SdfNodeDesc *source) {
  LubSdfNodeDesc result = {0};
  if (!source)
    return result;
  result.op = source->f_op;
  result.a = source->f_a;
  result.b = source->f_b;
  if (source->f_params) {
    result.params_count = tvm_list_count(source->f_params);
    if (result.params_count > 8) {
      vm_lub_fail("lub-array-length");
      result.params_count = 0;
    }
    memcpy(result.params, tvm_list_data(source->f_params),
           (size_t)result.params_count * sizeof(*result.params));
  }
  result.name = vm_lub_str(source->f_name);
  return result;
}
static LubPlayOpts vm_lub_to_PlayOpts(VmLub_PlayOpts *source) {
  LubPlayOpts result = {0};
  if (!source)
    return result;
  if (source->f_volume.has) {
    result.has_volume = true;
    result.volume = source->f_volume.v;
  }
  if (source->f_pitch.has) {
    result.has_pitch = true;
    result.pitch = source->f_pitch.v;
  }
  if (source->f_pan.has) {
    result.has_pan = true;
    result.pan = source->f_pan.v;
  }
  return result;
}
static LubVoiceOpts vm_lub_to_VoiceOpts(VmLub_VoiceOpts *source) {
  LubVoiceOpts result = {0};
  if (!source)
    return result;
  if (source->f_volume.has) {
    result.base.has_volume = true;
    result.base.volume = source->f_volume.v;
  }
  if (source->f_pitch.has) {
    result.base.has_pitch = true;
    result.base.pitch = source->f_pitch.v;
  }
  if (source->f_pan.has) {
    result.base.has_pan = true;
    result.base.pan = source->f_pan.v;
  }
  if (source->f_loop.has) {
    result.has_loop = true;
    result.loop = source->f_loop.v;
  }
  return result;
}
static LubWorldOpts vm_lub_to_WorldOpts(VmLub_WorldOpts *source) {
  LubWorldOpts result = {0};
  if (!source)
    return result;
  if (source->f_version.has) {
    result.has_version = true;
    result.version = source->f_version.v;
  }
  if (source->f_gravity) {
    result.has_gravity = true;
    result.gravity = vm_lub_to_Vec2d(source->f_gravity);
  }
  if (source->f_fixed_dt.has) {
    result.has_fixed_dt = true;
    result.fixed_dt = source->f_fixed_dt.v;
  }
  if (source->f_substeps.has) {
    result.has_substeps = true;
    result.substeps = source->f_substeps.v;
  }
  if (source->f_max_steps.has) {
    result.has_max_steps = true;
    result.max_steps = source->f_max_steps.v;
  }
  if (source->f_sleep.has) {
    result.has_sleep = true;
    result.sleep = source->f_sleep.v;
  }
  if (source->f_continuous.has) {
    result.has_continuous = true;
    result.continuous = source->f_continuous.v;
  }
  if (source->f_hit_event_threshold.has) {
    result.has_hit_event_threshold = true;
    result.hit_event_threshold = source->f_hit_event_threshold.v;
  }
  if (source->f_callbacks) {
    result.has_callbacks = true;
    result.callbacks = vm_lub_to_WorldCallbacks(source->f_callbacks);
  }
  return result;
}
static LubVec2d vm_lub_to_Vec2d(VmLub_Vec2d *source) {
  LubVec2d result = {0};
  if (!source)
    return result;
  result.x = source->f_x;
  result.y = source->f_y;
  return result;
}
static LubWorldCallbacks
vm_lub_to_WorldCallbacks(VmLub_WorldCallbacks *source) {
  LubWorldCallbacks result = {0};
  if (!source)
    return result;
  if (source->f_filter)
    vm_lub_fail("lub-callback-field: WorldCallbacks.Filter");
  if (source->f_pre_solve)
    vm_lub_fail("lub-callback-field: WorldCallbacks.PreSolve");
  if (source->f_friction)
    vm_lub_fail("lub-callback-field: WorldCallbacks.Friction");
  if (source->f_restitution)
    vm_lub_fail("lub-callback-field: WorldCallbacks.Restitution");
  return result;
}
static LubBeginOpts vm_lub_to_BeginOpts(VmLub_BeginOpts *source) {
  LubBeginOpts result = {0};
  if (!source)
    return result;
  if (source->f_prune.has) {
    result.has_prune = true;
    result.prune = source->f_prune.v;
  }
  return result;
}
static LubBodyDesc vm_lub_to_BodyDesc(VmLub_BodyDesc *source) {
  LubBodyDesc result = {0};
  if (!source)
    return result;
  if (source->f_version.has) {
    result.has_version = true;
    result.version = source->f_version.v;
  }
  if (source->f_type.has) {
    result.has_type = true;
    result.type = source->f_type.v;
  }
  if (source->f_fixed_rotation.has) {
    result.has_fixed_rotation = true;
    result.fixed_rotation = source->f_fixed_rotation.v;
  }
  if (source->f_bullet.has) {
    result.has_bullet = true;
    result.bullet = source->f_bullet.v;
  }
  if (source->f_enabled.has) {
    result.has_enabled = true;
    result.enabled = source->f_enabled.v;
  }
  if (source->f_awake.has) {
    result.has_awake = true;
    result.awake = source->f_awake.v;
  }
  if (source->f_sleep.has) {
    result.has_sleep = true;
    result.sleep = source->f_sleep.v;
  }
  if (source->f_sleep_threshold.has) {
    result.has_sleep_threshold = true;
    result.sleep_threshold = source->f_sleep_threshold.v;
  }
  if (source->f_gravity_scale.has) {
    result.has_gravity_scale = true;
    result.gravity_scale = source->f_gravity_scale.v;
  }
  if (source->f_linear_damping.has) {
    result.has_linear_damping = true;
    result.linear_damping = source->f_linear_damping.v;
  }
  if (source->f_angular_damping.has) {
    result.has_angular_damping = true;
    result.angular_damping = source->f_angular_damping.v;
  }
  if (source->f_initial) {
    result.has_initial = true;
    result.initial = vm_lub_to_InitialState(source->f_initial);
  }
  return result;
}
static LubInitialState vm_lub_to_InitialState(VmLub_InitialState *source) {
  LubInitialState result = {0};
  if (!source)
    return result;
  if (source->f_x.has) {
    result.has_x = true;
    result.x = source->f_x.v;
  }
  if (source->f_y.has) {
    result.has_y = true;
    result.y = source->f_y.v;
  }
  if (source->f_angle.has) {
    result.has_angle = true;
    result.angle = source->f_angle.v;
  }
  if (source->f_vx.has) {
    result.has_vx = true;
    result.vx = source->f_vx.v;
  }
  if (source->f_vy.has) {
    result.has_vy = true;
    result.vy = source->f_vy.v;
  }
  if (source->f_w.has) {
    result.has_w = true;
    result.w = source->f_w.v;
  }
  if (source->f_awake.has) {
    result.has_awake = true;
    result.awake = source->f_awake.v;
  }
  return result;
}
static LubBoxDesc vm_lub_to_BoxDesc(VmLub_BoxDesc *source) {
  LubBoxDesc result = {0};
  if (!source)
    return result;
  if (source->f_version.has) {
    result.base.has_version = true;
    result.base.version = source->f_version.v;
  }
  if (source->f_density.has) {
    result.base.has_density = true;
    result.base.density = source->f_density.v;
  }
  if (source->f_friction.has) {
    result.base.has_friction = true;
    result.base.friction = source->f_friction.v;
  }
  if (source->f_restitution.has) {
    result.base.has_restitution = true;
    result.base.restitution = source->f_restitution.v;
  }
  result.base.tag = vm_lub_str(source->f_tag);
  result.base.material_name = vm_lub_str(source->f_material_name);
  if (source->f_material_id.has) {
    result.base.has_material_id = true;
    result.base.material_id = source->f_material_id.v;
  }
  if (source->f_sensor.has) {
    result.base.has_sensor = true;
    result.base.sensor = source->f_sensor.v;
  }
  if (source->f_contact.has) {
    result.base.has_contact = true;
    result.base.contact = source->f_contact.v;
  }
  if (source->f_hit.has) {
    result.base.has_hit = true;
    result.base.hit = source->f_hit.v;
  }
  if (source->f_sensor_events.has) {
    result.base.has_sensor_events = true;
    result.base.sensor_events = source->f_sensor_events.v;
  }
  if (source->f_pre_solve.has) {
    result.base.has_pre_solve = true;
    result.base.pre_solve = source->f_pre_solve.v;
  }
  if (source->f_filter) {
    result.base.has_filter = true;
    result.base.filter = vm_lub_to_FilterDesc(source->f_filter);
  }
  result.hx = source->f_hx;
  result.hy = source->f_hy;
  if (source->f_cx.has) {
    result.has_cx = true;
    result.cx = source->f_cx.v;
  }
  if (source->f_cy.has) {
    result.has_cy = true;
    result.cy = source->f_cy.v;
  }
  if (source->f_angle.has) {
    result.has_angle = true;
    result.angle = source->f_angle.v;
  }
  return result;
}
static LubFilterDesc vm_lub_to_FilterDesc(VmLub_FilterDesc *source) {
  LubFilterDesc result = {0};
  if (!source)
    return result;
  result.has_category_bits =
      vm_lub_bits(source->f_category_bits, &result.category_bits);
  result.has_mask_bits = vm_lub_bits(source->f_mask_bits, &result.mask_bits);
  if (source->f_group.has) {
    result.has_group = true;
    result.group = source->f_group.v;
  }
  return result;
}
static LubCircleDesc vm_lub_to_CircleDesc(VmLub_CircleDesc *source) {
  LubCircleDesc result = {0};
  if (!source)
    return result;
  if (source->f_version.has) {
    result.base.has_version = true;
    result.base.version = source->f_version.v;
  }
  if (source->f_density.has) {
    result.base.has_density = true;
    result.base.density = source->f_density.v;
  }
  if (source->f_friction.has) {
    result.base.has_friction = true;
    result.base.friction = source->f_friction.v;
  }
  if (source->f_restitution.has) {
    result.base.has_restitution = true;
    result.base.restitution = source->f_restitution.v;
  }
  result.base.tag = vm_lub_str(source->f_tag);
  result.base.material_name = vm_lub_str(source->f_material_name);
  if (source->f_material_id.has) {
    result.base.has_material_id = true;
    result.base.material_id = source->f_material_id.v;
  }
  if (source->f_sensor.has) {
    result.base.has_sensor = true;
    result.base.sensor = source->f_sensor.v;
  }
  if (source->f_contact.has) {
    result.base.has_contact = true;
    result.base.contact = source->f_contact.v;
  }
  if (source->f_hit.has) {
    result.base.has_hit = true;
    result.base.hit = source->f_hit.v;
  }
  if (source->f_sensor_events.has) {
    result.base.has_sensor_events = true;
    result.base.sensor_events = source->f_sensor_events.v;
  }
  if (source->f_pre_solve.has) {
    result.base.has_pre_solve = true;
    result.base.pre_solve = source->f_pre_solve.v;
  }
  if (source->f_filter) {
    result.base.has_filter = true;
    result.base.filter = vm_lub_to_FilterDesc(source->f_filter);
  }
  result.r = source->f_r;
  if (source->f_cx.has) {
    result.has_cx = true;
    result.cx = source->f_cx.v;
  }
  if (source->f_cy.has) {
    result.has_cy = true;
    result.cy = source->f_cy.v;
  }
  return result;
}
static LubCapsuleDesc vm_lub_to_CapsuleDesc(VmLub_CapsuleDesc *source) {
  LubCapsuleDesc result = {0};
  if (!source)
    return result;
  if (source->f_version.has) {
    result.base.has_version = true;
    result.base.version = source->f_version.v;
  }
  if (source->f_density.has) {
    result.base.has_density = true;
    result.base.density = source->f_density.v;
  }
  if (source->f_friction.has) {
    result.base.has_friction = true;
    result.base.friction = source->f_friction.v;
  }
  if (source->f_restitution.has) {
    result.base.has_restitution = true;
    result.base.restitution = source->f_restitution.v;
  }
  result.base.tag = vm_lub_str(source->f_tag);
  result.base.material_name = vm_lub_str(source->f_material_name);
  if (source->f_material_id.has) {
    result.base.has_material_id = true;
    result.base.material_id = source->f_material_id.v;
  }
  if (source->f_sensor.has) {
    result.base.has_sensor = true;
    result.base.sensor = source->f_sensor.v;
  }
  if (source->f_contact.has) {
    result.base.has_contact = true;
    result.base.contact = source->f_contact.v;
  }
  if (source->f_hit.has) {
    result.base.has_hit = true;
    result.base.hit = source->f_hit.v;
  }
  if (source->f_sensor_events.has) {
    result.base.has_sensor_events = true;
    result.base.sensor_events = source->f_sensor_events.v;
  }
  if (source->f_pre_solve.has) {
    result.base.has_pre_solve = true;
    result.base.pre_solve = source->f_pre_solve.v;
  }
  if (source->f_filter) {
    result.base.has_filter = true;
    result.base.filter = vm_lub_to_FilterDesc(source->f_filter);
  }
  result.ax = source->f_ax;
  result.ay = source->f_ay;
  result.bx = source->f_bx;
  result.by = source->f_by;
  result.r = source->f_r;
  return result;
}
static LubSegmentDesc vm_lub_to_SegmentDesc(VmLub_SegmentDesc *source) {
  LubSegmentDesc result = {0};
  if (!source)
    return result;
  if (source->f_version.has) {
    result.base.has_version = true;
    result.base.version = source->f_version.v;
  }
  if (source->f_density.has) {
    result.base.has_density = true;
    result.base.density = source->f_density.v;
  }
  if (source->f_friction.has) {
    result.base.has_friction = true;
    result.base.friction = source->f_friction.v;
  }
  if (source->f_restitution.has) {
    result.base.has_restitution = true;
    result.base.restitution = source->f_restitution.v;
  }
  result.base.tag = vm_lub_str(source->f_tag);
  result.base.material_name = vm_lub_str(source->f_material_name);
  if (source->f_material_id.has) {
    result.base.has_material_id = true;
    result.base.material_id = source->f_material_id.v;
  }
  if (source->f_sensor.has) {
    result.base.has_sensor = true;
    result.base.sensor = source->f_sensor.v;
  }
  if (source->f_contact.has) {
    result.base.has_contact = true;
    result.base.contact = source->f_contact.v;
  }
  if (source->f_hit.has) {
    result.base.has_hit = true;
    result.base.hit = source->f_hit.v;
  }
  if (source->f_sensor_events.has) {
    result.base.has_sensor_events = true;
    result.base.sensor_events = source->f_sensor_events.v;
  }
  if (source->f_pre_solve.has) {
    result.base.has_pre_solve = true;
    result.base.pre_solve = source->f_pre_solve.v;
  }
  if (source->f_filter) {
    result.base.has_filter = true;
    result.base.filter = vm_lub_to_FilterDesc(source->f_filter);
  }
  result.ax = source->f_ax;
  result.ay = source->f_ay;
  result.bx = source->f_bx;
  result.by = source->f_by;
  return result;
}
static LubPolygonDesc vm_lub_to_PolygonDesc(VmLub_PolygonDesc *source) {
  LubPolygonDesc result = {0};
  if (!source)
    return result;
  if (source->f_version.has) {
    result.base.has_version = true;
    result.base.version = source->f_version.v;
  }
  if (source->f_density.has) {
    result.base.has_density = true;
    result.base.density = source->f_density.v;
  }
  if (source->f_friction.has) {
    result.base.has_friction = true;
    result.base.friction = source->f_friction.v;
  }
  if (source->f_restitution.has) {
    result.base.has_restitution = true;
    result.base.restitution = source->f_restitution.v;
  }
  result.base.tag = vm_lub_str(source->f_tag);
  result.base.material_name = vm_lub_str(source->f_material_name);
  if (source->f_material_id.has) {
    result.base.has_material_id = true;
    result.base.material_id = source->f_material_id.v;
  }
  if (source->f_sensor.has) {
    result.base.has_sensor = true;
    result.base.sensor = source->f_sensor.v;
  }
  if (source->f_contact.has) {
    result.base.has_contact = true;
    result.base.contact = source->f_contact.v;
  }
  if (source->f_hit.has) {
    result.base.has_hit = true;
    result.base.hit = source->f_hit.v;
  }
  if (source->f_sensor_events.has) {
    result.base.has_sensor_events = true;
    result.base.sensor_events = source->f_sensor_events.v;
  }
  if (source->f_pre_solve.has) {
    result.base.has_pre_solve = true;
    result.base.pre_solve = source->f_pre_solve.v;
  }
  if (source->f_filter) {
    result.base.has_filter = true;
    result.base.filter = vm_lub_to_FilterDesc(source->f_filter);
  }
  if (source->f_points) {
    result.points_count = tvm_list_count(source->f_points);
    result.points = tvm_list_data(source->f_points);
  }
  if (source->f_radius.has) {
    result.has_radius = true;
    result.radius = source->f_radius.v;
  }
  if (source->f_cx.has) {
    result.has_cx = true;
    result.cx = source->f_cx.v;
  }
  if (source->f_cy.has) {
    result.has_cy = true;
    result.cy = source->f_cy.v;
  }
  if (source->f_angle.has) {
    result.has_angle = true;
    result.angle = source->f_angle.v;
  }
  return result;
}
static LubChainDesc vm_lub_to_ChainDesc(VmLub_ChainDesc *source) {
  LubChainDesc result = {0};
  if (!source)
    return result;
  result.version = source->f_version;
  if (source->f_points) {
    result.points_count = tvm_list_count(source->f_points);
    result.points = tvm_list_data(source->f_points);
  }
  if (source->f_materials) {
    result.materials = vm_lub_to_ChainMaterial_list(source->f_materials,
                                                    &result.materials_count);
  }
  if (source->f_loop.has) {
    result.has_loop = true;
    result.loop = source->f_loop.v;
  }
  if (source->f_friction.has) {
    result.has_friction = true;
    result.friction = source->f_friction.v;
  }
  if (source->f_restitution.has) {
    result.has_restitution = true;
    result.restitution = source->f_restitution.v;
  }
  result.tag = vm_lub_str(source->f_tag);
  result.material_name = vm_lub_str(source->f_material_name);
  if (source->f_material_id.has) {
    result.has_material_id = true;
    result.material_id = source->f_material_id.v;
  }
  if (source->f_sensor_events.has) {
    result.has_sensor_events = true;
    result.sensor_events = source->f_sensor_events.v;
  }
  if (source->f_filter) {
    result.has_filter = true;
    result.filter = vm_lub_to_FilterDesc(source->f_filter);
  }
  return result;
}
static LubChainMaterial vm_lub_to_ChainMaterial(VmLub_ChainMaterial *source) {
  LubChainMaterial result = {0};
  if (!source)
    return result;
  if (source->f_friction.has) {
    result.has_friction = true;
    result.friction = source->f_friction.v;
  }
  if (source->f_restitution.has) {
    result.has_restitution = true;
    result.restitution = source->f_restitution.v;
  }
  if (source->f_material_id.has) {
    result.has_material_id = true;
    result.material_id = source->f_material_id.v;
  }
  return result;
}
static LubJointDesc vm_lub_to_JointDesc(VmLub_JointDesc *source) {
  LubJointDesc result = {0};
  if (!source)
    return result;
  if (source->f_version.has) {
    result.has_version = true;
    result.version = source->f_version.v;
  }
  if (source->f_type.has) {
    result.has_type = true;
    result.type = source->f_type.v;
  }
  result.body_a =
      (source->f_body_a ? (int32_t)source->f_body_a->host_value : 0);
  result.body_b =
      (source->f_body_b ? (int32_t)source->f_body_b->host_value : 0);
  if (source->f_anchor_a) {
    result.has_anchor_a = true;
    result.anchor_a = vm_lub_to_Vec2d(source->f_anchor_a);
  }
  if (source->f_anchor_b) {
    result.has_anchor_b = true;
    result.anchor_b = vm_lub_to_Vec2d(source->f_anchor_b);
  }
  if (source->f_local_anchor_a) {
    result.has_local_anchor_a = true;
    result.local_anchor_a = vm_lub_to_Vec2d(source->f_local_anchor_a);
  }
  if (source->f_local_anchor_b) {
    result.has_local_anchor_b = true;
    result.local_anchor_b = vm_lub_to_Vec2d(source->f_local_anchor_b);
  }
  if (source->f_local_axis_a) {
    result.has_local_axis_a = true;
    result.local_axis_a = vm_lub_to_Vec2d(source->f_local_axis_a);
  }
  if (source->f_reference_angle.has) {
    result.has_reference_angle = true;
    result.reference_angle = source->f_reference_angle.v;
  }
  if (source->f_collide_connected.has) {
    result.has_collide_connected = true;
    result.collide_connected = source->f_collide_connected.v;
  }
  if (source->f_length.has) {
    result.has_length = true;
    result.length = source->f_length.v;
  }
  if (source->f_min_length.has) {
    result.has_min_length = true;
    result.min_length = source->f_min_length.v;
  }
  if (source->f_max_length.has) {
    result.has_max_length = true;
    result.max_length = source->f_max_length.v;
  }
  if (source->f_lower.has) {
    result.has_lower = true;
    result.lower = source->f_lower.v;
  }
  if (source->f_upper.has) {
    result.has_upper = true;
    result.upper = source->f_upper.v;
  }
  if (source->f_target_angle.has) {
    result.has_target_angle = true;
    result.target_angle = source->f_target_angle.v;
  }
  if (source->f_target_translation.has) {
    result.has_target_translation = true;
    result.target_translation = source->f_target_translation.v;
  }
  if (source->f_linear_offset) {
    result.has_linear_offset = true;
    result.linear_offset = vm_lub_to_Vec2d(source->f_linear_offset);
  }
  if (source->f_angular_offset.has) {
    result.has_angular_offset = true;
    result.angular_offset = source->f_angular_offset.v;
  }
  if (source->f_hertz.has) {
    result.has_hertz = true;
    result.hertz = source->f_hertz.v;
  }
  if (source->f_damping_ratio.has) {
    result.has_damping_ratio = true;
    result.damping_ratio = source->f_damping_ratio.v;
  }
  if (source->f_max_force.has) {
    result.has_max_force = true;
    result.max_force = source->f_max_force.v;
  }
  if (source->f_max_torque.has) {
    result.has_max_torque = true;
    result.max_torque = source->f_max_torque.v;
  }
  if (source->f_motor_speed.has) {
    result.has_motor_speed = true;
    result.motor_speed = source->f_motor_speed.v;
  }
  if (source->f_correction_factor.has) {
    result.has_correction_factor = true;
    result.correction_factor = source->f_correction_factor.v;
  }
  if (source->f_spring) {
    result.has_spring = true;
    result.spring = vm_lub_to_JointSpringDesc(source->f_spring);
  }
  if (source->f_limit) {
    result.has_limit = true;
    result.limit = vm_lub_to_JointLimitDesc(source->f_limit);
  }
  if (source->f_motor) {
    result.has_motor = true;
    result.motor = vm_lub_to_JointMotorDesc(source->f_motor);
  }
  if (source->f_target) {
    result.has_target = true;
    result.target = vm_lub_to_Vec2d(source->f_target);
  }
  return result;
}
static LubJointSpringDesc
vm_lub_to_JointSpringDesc(VmLub_JointSpringDesc *source) {
  LubJointSpringDesc result = {0};
  if (!source)
    return result;
  if (source->f_enabled.has) {
    result.has_enabled = true;
    result.enabled = source->f_enabled.v;
  }
  if (source->f_hertz.has) {
    result.has_hertz = true;
    result.hertz = source->f_hertz.v;
  }
  if (source->f_damping_ratio.has) {
    result.has_damping_ratio = true;
    result.damping_ratio = source->f_damping_ratio.v;
  }
  if (source->f_linear_hertz.has) {
    result.has_linear_hertz = true;
    result.linear_hertz = source->f_linear_hertz.v;
  }
  if (source->f_linear_damping_ratio.has) {
    result.has_linear_damping_ratio = true;
    result.linear_damping_ratio = source->f_linear_damping_ratio.v;
  }
  if (source->f_angular_hertz.has) {
    result.has_angular_hertz = true;
    result.angular_hertz = source->f_angular_hertz.v;
  }
  if (source->f_angular_damping_ratio.has) {
    result.has_angular_damping_ratio = true;
    result.angular_damping_ratio = source->f_angular_damping_ratio.v;
  }
  return result;
}
static LubJointLimitDesc
vm_lub_to_JointLimitDesc(VmLub_JointLimitDesc *source) {
  LubJointLimitDesc result = {0};
  if (!source)
    return result;
  if (source->f_enabled.has) {
    result.has_enabled = true;
    result.enabled = source->f_enabled.v;
  }
  if (source->f_lower.has) {
    result.has_lower = true;
    result.lower = source->f_lower.v;
  }
  if (source->f_upper.has) {
    result.has_upper = true;
    result.upper = source->f_upper.v;
  }
  if (source->f_min_length.has) {
    result.has_min_length = true;
    result.min_length = source->f_min_length.v;
  }
  if (source->f_max_length.has) {
    result.has_max_length = true;
    result.max_length = source->f_max_length.v;
  }
  return result;
}
static LubJointMotorDesc
vm_lub_to_JointMotorDesc(VmLub_JointMotorDesc *source) {
  LubJointMotorDesc result = {0};
  if (!source)
    return result;
  if (source->f_enabled.has) {
    result.has_enabled = true;
    result.enabled = source->f_enabled.v;
  }
  if (source->f_speed.has) {
    result.has_speed = true;
    result.speed = source->f_speed.v;
  }
  if (source->f_max_force.has) {
    result.has_max_force = true;
    result.max_force = source->f_max_force.v;
  }
  if (source->f_max_torque.has) {
    result.has_max_torque = true;
    result.max_torque = source->f_max_torque.v;
  }
  if (source->f_linear_offset) {
    result.has_linear_offset = true;
    result.linear_offset = vm_lub_to_Vec2d(source->f_linear_offset);
  }
  if (source->f_angular_offset.has) {
    result.has_angular_offset = true;
    result.angular_offset = source->f_angular_offset.v;
  }
  if (source->f_correction_factor.has) {
    result.has_correction_factor = true;
    result.correction_factor = source->f_correction_factor.v;
  }
  return result;
}
static LubJointTargetDesc
vm_lub_to_JointTargetDesc(VmLub_JointTargetDesc *source) {
  LubJointTargetDesc result = {0};
  if (!source)
    return result;
  if (source->f_x.has) {
    result.has_x = true;
    result.x = source->f_x.v;
  }
  if (source->f_y.has) {
    result.has_y = true;
    result.y = source->f_y.v;
  }
  if (source->f_translation.has) {
    result.has_translation = true;
    result.translation = source->f_translation.v;
  }
  if (source->f_angle.has) {
    result.has_angle = true;
    result.angle = source->f_angle.v;
  }
  if (source->f_linear_offset) {
    result.has_linear_offset = true;
    result.linear_offset = vm_lub_to_Vec2d(source->f_linear_offset);
  }
  if (source->f_angular_offset.has) {
    result.has_angular_offset = true;
    result.angular_offset = source->f_angular_offset.v;
  }
  return result;
}
static LubRaycastDesc vm_lub_to_RaycastDesc(VmLub_RaycastDesc *source) {
  LubRaycastDesc result = {0};
  if (!source)
    return result;
  if (source->f_x.has) {
    result.has_x = true;
    result.x = source->f_x.v;
  }
  if (source->f_y.has) {
    result.has_y = true;
    result.y = source->f_y.v;
  }
  if (source->f_dx.has) {
    result.has_dx = true;
    result.dx = source->f_dx.v;
  }
  if (source->f_dy.has) {
    result.has_dy = true;
    result.dy = source->f_dy.v;
  }
  if (source->f_max_fraction.has) {
    result.has_max_fraction = true;
    result.max_fraction = source->f_max_fraction.v;
  }
  if (source->f_filter) {
    result.has_filter = true;
    result.filter = vm_lub_to_FilterDesc(source->f_filter);
  }
  return result;
}
static LubMaterialDesc vm_lub_to_MaterialDesc(VmLub_MaterialDesc *source) {
  LubMaterialDesc result = {0};
  if (!source)
    return result;
  if (source->f_density.has) {
    result.has_density = true;
    result.density = source->f_density.v;
  }
  if (source->f_friction.has) {
    result.has_friction = true;
    result.friction = source->f_friction.v;
  }
  if (source->f_restitution.has) {
    result.has_restitution = true;
    result.restitution = source->f_restitution.v;
  }
  result.material_name = vm_lub_str(source->f_material_name);
  if (source->f_material_id.has) {
    result.has_material_id = true;
    result.material_id = source->f_material_id.v;
  }
  return result;
}
static LubShapeEventsDesc
vm_lub_to_ShapeEventsDesc(VmLub_ShapeEventsDesc *source) {
  LubShapeEventsDesc result = {0};
  if (!source)
    return result;
  if (source->f_sensor_events.has) {
    result.has_sensor_events = true;
    result.sensor_events = source->f_sensor_events.v;
  }
  if (source->f_contact.has) {
    result.has_contact = true;
    result.contact = source->f_contact.v;
  }
  if (source->f_pre_solve.has) {
    result.has_pre_solve = true;
    result.pre_solve = source->f_pre_solve.v;
  }
  if (source->f_hit.has) {
    result.has_hit = true;
    result.hit = source->f_hit.v;
  }
  return result;
}
static LubAabbDesc vm_lub_to_AabbDesc(VmLub_AabbDesc *source) {
  LubAabbDesc result = {0};
  if (!source)
    return result;
  result.min_x = source->f_min_x;
  result.min_y = source->f_min_y;
  result.max_x = source->f_max_x;
  result.max_y = source->f_max_y;
  if (source->f_filter) {
    result.has_filter = true;
    result.filter = vm_lub_to_FilterDesc(source->f_filter);
  }
  return result;
}
static LubShapeCastDesc vm_lub_to_ShapeCastDesc(VmLub_ShapeCastDesc *source) {
  LubShapeCastDesc result = {0};
  if (!source)
    return result;
  if (source->f_kind.has) {
    result.has_kind = true;
    result.kind = source->f_kind.v;
  }
  if (source->f_x.has) {
    result.has_x = true;
    result.x = source->f_x.v;
  }
  if (source->f_y.has) {
    result.has_y = true;
    result.y = source->f_y.v;
  }
  if (source->f_angle.has) {
    result.has_angle = true;
    result.angle = source->f_angle.v;
  }
  if (source->f_radius.has) {
    result.has_radius = true;
    result.radius = source->f_radius.v;
  }
  if (source->f_cx.has) {
    result.has_cx = true;
    result.cx = source->f_cx.v;
  }
  if (source->f_cy.has) {
    result.has_cy = true;
    result.cy = source->f_cy.v;
  }
  if (source->f_ax.has) {
    result.has_ax = true;
    result.ax = source->f_ax.v;
  }
  if (source->f_ay.has) {
    result.has_ay = true;
    result.ay = source->f_ay.v;
  }
  if (source->f_bx.has) {
    result.has_bx = true;
    result.bx = source->f_bx.v;
  }
  if (source->f_by.has) {
    result.has_by = true;
    result.by = source->f_by.v;
  }
  if (source->f_hx.has) {
    result.has_hx = true;
    result.hx = source->f_hx.v;
  }
  if (source->f_hy.has) {
    result.has_hy = true;
    result.hy = source->f_hy.v;
  }
  if (source->f_points) {
    result.points_count = tvm_list_count(source->f_points);
    result.points = tvm_list_data(source->f_points);
  }
  if (source->f_dx.has) {
    result.has_dx = true;
    result.dx = source->f_dx.v;
  }
  if (source->f_dy.has) {
    result.has_dy = true;
    result.dy = source->f_dy.v;
  }
  if (source->f_max_fraction.has) {
    result.has_max_fraction = true;
    result.max_fraction = source->f_max_fraction.v;
  }
  if (source->f_filter) {
    result.has_filter = true;
    result.filter = vm_lub_to_FilterDesc(source->f_filter);
  }
  return result;
}
static LubMoverDesc vm_lub_to_MoverDesc(VmLub_MoverDesc *source) {
  LubMoverDesc result = {0};
  if (!source)
    return result;
  result.ax = source->f_ax;
  result.ay = source->f_ay;
  result.bx = source->f_bx;
  result.by = source->f_by;
  result.r = source->f_r;
  if (source->f_dx.has) {
    result.has_dx = true;
    result.dx = source->f_dx.v;
  }
  if (source->f_dy.has) {
    result.has_dy = true;
    result.dy = source->f_dy.v;
  }
  if (source->f_max_fraction.has) {
    result.has_max_fraction = true;
    result.max_fraction = source->f_max_fraction.v;
  }
  if (source->f_filter) {
    result.has_filter = true;
    result.filter = vm_lub_to_FilterDesc(source->f_filter);
  }
  return result;
}
static LubExplosionDesc vm_lub_to_ExplosionDesc(VmLub_ExplosionDesc *source) {
  LubExplosionDesc result = {0};
  if (!source)
    return result;
  if (source->f_x.has) {
    result.has_x = true;
    result.x = source->f_x.v;
  }
  if (source->f_y.has) {
    result.has_y = true;
    result.y = source->f_y.v;
  }
  if (source->f_radius.has) {
    result.has_radius = true;
    result.radius = source->f_radius.v;
  }
  if (source->f_falloff.has) {
    result.has_falloff = true;
    result.falloff = source->f_falloff.v;
  }
  if (source->f_impulse_per_length.has) {
    result.has_impulse_per_length = true;
    result.impulse_per_length = source->f_impulse_per_length.v;
  }
  if (source->f_filter) {
    result.has_filter = true;
    result.filter = vm_lub_to_FilterDesc(source->f_filter);
  }
  return result;
}
static LubDebugOpts vm_lub_to_DebugOpts(VmLub_DebugOpts *source) {
  LubDebugOpts result = {0};
  if (!source)
    return result;
  if (source->f_shapes.has) {
    result.has_shapes = true;
    result.shapes = source->f_shapes.v;
  }
  if (source->f_joints.has) {
    result.has_joints = true;
    result.joints = source->f_joints.v;
  }
  if (source->f_joint_extras.has) {
    result.has_joint_extras = true;
    result.joint_extras = source->f_joint_extras.v;
  }
  if (source->f_bounds.has) {
    result.has_bounds = true;
    result.bounds = source->f_bounds.v;
  }
  if (source->f_mass.has) {
    result.has_mass = true;
    result.mass = source->f_mass.v;
  }
  if (source->f_body_names.has) {
    result.has_body_names = true;
    result.body_names = source->f_body_names.v;
  }
  if (source->f_contacts.has) {
    result.has_contacts = true;
    result.contacts = source->f_contacts.v;
  }
  if (source->f_graph_colors.has) {
    result.has_graph_colors = true;
    result.graph_colors = source->f_graph_colors.v;
  }
  if (source->f_contact_normals.has) {
    result.has_contact_normals = true;
    result.contact_normals = source->f_contact_normals.v;
  }
  if (source->f_contact_impulses.has) {
    result.has_contact_impulses = true;
    result.contact_impulses = source->f_contact_impulses.v;
  }
  if (source->f_contact_features.has) {
    result.has_contact_features = true;
    result.contact_features = source->f_contact_features.v;
  }
  if (source->f_friction_impulses.has) {
    result.has_friction_impulses = true;
    result.friction_impulses = source->f_friction_impulses.v;
  }
  if (source->f_islands.has) {
    result.has_islands = true;
    result.islands = source->f_islands.v;
  }
  if (source->f_drawing_bounds) {
    result.has_drawing_bounds = true;
    result.drawing_bounds = vm_lub_to_AabbDesc(source->f_drawing_bounds);
  }
  return result;
}
static LubCommandOpts vm_lub_to_CommandOpts(VmLub_CommandOpts *source) {
  LubCommandOpts result = {0};
  if (!source)
    return result;
  if (source->f_wake.has) {
    result.has_wake = true;
    result.wake = source->f_wake.v;
  }
  if (source->f_point) {
    result.has_point = true;
    result.point = vm_lub_to_Vec2d(source->f_point);
  }
  if (source->f_time_step.has) {
    result.has_time_step = true;
    result.time_step = source->f_time_step.v;
  }
  return result;
}
static LubVelocityDesc vm_lub_to_VelocityDesc(VmLub_VelocityDesc *source) {
  LubVelocityDesc result = {0};
  if (!source)
    return result;
  if (source->f_vx.has) {
    result.has_vx = true;
    result.vx = source->f_vx.v;
  }
  if (source->f_vy.has) {
    result.has_vy = true;
    result.vy = source->f_vy.v;
  }
  if (source->f_w.has) {
    result.has_w = true;
    result.w = source->f_w.v;
  }
  return result;
}
static LubPoseDesc vm_lub_to_PoseDesc(VmLub_PoseDesc *source) {
  LubPoseDesc result = {0};
  if (!source)
    return result;
  if (source->f_x.has) {
    result.has_x = true;
    result.x = source->f_x.v;
  }
  if (source->f_y.has) {
    result.has_y = true;
    result.y = source->f_y.v;
  }
  if (source->f_angle.has) {
    result.has_angle = true;
    result.angle = source->f_angle.v;
  }
  return result;
}
static LubMassDataDesc vm_lub_to_MassDataDesc(VmLub_MassDataDesc *source) {
  LubMassDataDesc result = {0};
  if (!source)
    return result;
  if (source->f_mass.has) {
    result.has_mass = true;
    result.mass = source->f_mass.v;
  }
  if (source->f_inertia.has) {
    result.has_inertia = true;
    result.inertia = source->f_inertia.v;
  }
  if (source->f_local_center) {
    result.has_local_center = true;
    result.local_center = vm_lub_to_Vec2d(source->f_local_center);
  }
  return result;
}
static LubWorldOpts3d vm_lub_to_WorldOpts3d(VmLub_WorldOpts3d *source) {
  LubWorldOpts3d result = {0};
  if (!source)
    return result;
  if (source->f_version.has) {
    result.has_version = true;
    result.version = source->f_version.v;
  }
  if (source->f_gravity) {
    result.has_gravity = true;
    result.gravity = vm_lub_to_Vec3d(source->f_gravity);
  }
  if (source->f_fixed_dt.has) {
    result.has_fixed_dt = true;
    result.fixed_dt = source->f_fixed_dt.v;
  }
  if (source->f_substeps.has) {
    result.has_substeps = true;
    result.substeps = source->f_substeps.v;
  }
  if (source->f_max_steps.has) {
    result.has_max_steps = true;
    result.max_steps = source->f_max_steps.v;
  }
  if (source->f_sleep.has) {
    result.has_sleep = true;
    result.sleep = source->f_sleep.v;
  }
  if (source->f_continuous.has) {
    result.has_continuous = true;
    result.continuous = source->f_continuous.v;
  }
  if (source->f_hit_event_threshold.has) {
    result.has_hit_event_threshold = true;
    result.hit_event_threshold = source->f_hit_event_threshold.v;
  }
  if (source->f_callbacks) {
    result.has_callbacks = true;
    result.callbacks = vm_lub_to_WorldCallbacks3d(source->f_callbacks);
  }
  return result;
}
static LubVec3d vm_lub_to_Vec3d(VmLub_Vec3d *source) {
  LubVec3d result = {0};
  if (!source)
    return result;
  result.x = source->f_x;
  result.y = source->f_y;
  result.z = source->f_z;
  return result;
}
static LubWorldCallbacks3d
vm_lub_to_WorldCallbacks3d(VmLub_WorldCallbacks3d *source) {
  LubWorldCallbacks3d result = {0};
  if (!source)
    return result;
  if (source->f_filter)
    vm_lub_fail("lub-callback-field: WorldCallbacks3d.Filter");
  if (source->f_pre_solve)
    vm_lub_fail("lub-callback-field: WorldCallbacks3d.PreSolve");
  if (source->f_friction)
    vm_lub_fail("lub-callback-field: WorldCallbacks3d.Friction");
  if (source->f_restitution)
    vm_lub_fail("lub-callback-field: WorldCallbacks3d.Restitution");
  return result;
}
static LubBeginOpts3d vm_lub_to_BeginOpts3d(VmLub_BeginOpts3d *source) {
  LubBeginOpts3d result = {0};
  if (!source)
    return result;
  if (source->f_prune.has) {
    result.has_prune = true;
    result.prune = source->f_prune.v;
  }
  return result;
}
static LubBodyDesc3d vm_lub_to_BodyDesc3d(VmLub_BodyDesc3d *source) {
  LubBodyDesc3d result = {0};
  if (!source)
    return result;
  if (source->f_version.has) {
    result.has_version = true;
    result.version = source->f_version.v;
  }
  if (source->f_type.has) {
    result.has_type = true;
    result.type = source->f_type.v;
  }
  if (source->f_motion_locks) {
    result.has_motion_locks = true;
    result.motion_locks = vm_lub_to_MotionLocks3d(source->f_motion_locks);
  }
  if (source->f_bullet.has) {
    result.has_bullet = true;
    result.bullet = source->f_bullet.v;
  }
  if (source->f_enabled.has) {
    result.has_enabled = true;
    result.enabled = source->f_enabled.v;
  }
  if (source->f_awake.has) {
    result.has_awake = true;
    result.awake = source->f_awake.v;
  }
  if (source->f_sleep.has) {
    result.has_sleep = true;
    result.sleep = source->f_sleep.v;
  }
  if (source->f_sleep_threshold.has) {
    result.has_sleep_threshold = true;
    result.sleep_threshold = source->f_sleep_threshold.v;
  }
  if (source->f_gravity_scale.has) {
    result.has_gravity_scale = true;
    result.gravity_scale = source->f_gravity_scale.v;
  }
  if (source->f_linear_damping.has) {
    result.has_linear_damping = true;
    result.linear_damping = source->f_linear_damping.v;
  }
  if (source->f_angular_damping.has) {
    result.has_angular_damping = true;
    result.angular_damping = source->f_angular_damping.v;
  }
  if (source->f_initial) {
    result.has_initial = true;
    result.initial = vm_lub_to_InitialState3d(source->f_initial);
  }
  return result;
}
static LubMotionLocks3d vm_lub_to_MotionLocks3d(VmLub_MotionLocks3d *source) {
  LubMotionLocks3d result = {0};
  if (!source)
    return result;
  if (source->f_linear_x.has) {
    result.has_linear_x = true;
    result.linear_x = source->f_linear_x.v;
  }
  if (source->f_linear_y.has) {
    result.has_linear_y = true;
    result.linear_y = source->f_linear_y.v;
  }
  if (source->f_linear_z.has) {
    result.has_linear_z = true;
    result.linear_z = source->f_linear_z.v;
  }
  if (source->f_angular_x.has) {
    result.has_angular_x = true;
    result.angular_x = source->f_angular_x.v;
  }
  if (source->f_angular_y.has) {
    result.has_angular_y = true;
    result.angular_y = source->f_angular_y.v;
  }
  if (source->f_angular_z.has) {
    result.has_angular_z = true;
    result.angular_z = source->f_angular_z.v;
  }
  return result;
}
static LubInitialState3d
vm_lub_to_InitialState3d(VmLub_InitialState3d *source) {
  LubInitialState3d result = {0};
  if (!source)
    return result;
  if (source->f_x.has) {
    result.has_x = true;
    result.x = source->f_x.v;
  }
  if (source->f_y.has) {
    result.has_y = true;
    result.y = source->f_y.v;
  }
  if (source->f_z.has) {
    result.has_z = true;
    result.z = source->f_z.v;
  }
  if (source->f_quat) {
    result.has_quat = true;
    result.quat = vm_lub_to_Quat3d(source->f_quat);
  }
  if (source->f_euler) {
    result.has_euler = true;
    result.euler = vm_lub_to_Vec3d(source->f_euler);
  }
  if (source->f_vx.has) {
    result.has_vx = true;
    result.vx = source->f_vx.v;
  }
  if (source->f_vy.has) {
    result.has_vy = true;
    result.vy = source->f_vy.v;
  }
  if (source->f_vz.has) {
    result.has_vz = true;
    result.vz = source->f_vz.v;
  }
  if (source->f_wx.has) {
    result.has_wx = true;
    result.wx = source->f_wx.v;
  }
  if (source->f_wy.has) {
    result.has_wy = true;
    result.wy = source->f_wy.v;
  }
  if (source->f_wz.has) {
    result.has_wz = true;
    result.wz = source->f_wz.v;
  }
  if (source->f_awake.has) {
    result.has_awake = true;
    result.awake = source->f_awake.v;
  }
  return result;
}
static LubQuat3d vm_lub_to_Quat3d(VmLub_Quat3d *source) {
  LubQuat3d result = {0};
  if (!source)
    return result;
  result.x = source->f_x;
  result.y = source->f_y;
  result.z = source->f_z;
  result.w = source->f_w;
  return result;
}
static LubSphereDesc3d vm_lub_to_SphereDesc3d(VmLub_SphereDesc3d *source) {
  LubSphereDesc3d result = {0};
  if (!source)
    return result;
  if (source->f_version.has) {
    result.base.has_version = true;
    result.base.version = source->f_version.v;
  }
  if (source->f_density.has) {
    result.base.has_density = true;
    result.base.density = source->f_density.v;
  }
  if (source->f_friction.has) {
    result.base.has_friction = true;
    result.base.friction = source->f_friction.v;
  }
  if (source->f_restitution.has) {
    result.base.has_restitution = true;
    result.base.restitution = source->f_restitution.v;
  }
  result.base.tag = vm_lub_str(source->f_tag);
  result.base.material_name = vm_lub_str(source->f_material_name);
  if (source->f_material_id.has) {
    result.base.has_material_id = true;
    result.base.material_id = source->f_material_id.v;
  }
  if (source->f_sensor.has) {
    result.base.has_sensor = true;
    result.base.sensor = source->f_sensor.v;
  }
  if (source->f_contact.has) {
    result.base.has_contact = true;
    result.base.contact = source->f_contact.v;
  }
  if (source->f_hit.has) {
    result.base.has_hit = true;
    result.base.hit = source->f_hit.v;
  }
  if (source->f_sensor_events.has) {
    result.base.has_sensor_events = true;
    result.base.sensor_events = source->f_sensor_events.v;
  }
  if (source->f_pre_solve.has) {
    result.base.has_pre_solve = true;
    result.base.pre_solve = source->f_pre_solve.v;
  }
  if (source->f_filter) {
    result.base.has_filter = true;
    result.base.filter = vm_lub_to_FilterDesc3d(source->f_filter);
  }
  result.r = source->f_r;
  if (source->f_offset) {
    result.has_offset = true;
    result.offset = vm_lub_to_Vec3d(source->f_offset);
  }
  return result;
}
static LubFilterDesc3d vm_lub_to_FilterDesc3d(VmLub_FilterDesc3d *source) {
  LubFilterDesc3d result = {0};
  if (!source)
    return result;
  result.has_category_bits =
      vm_lub_bits(source->f_category_bits, &result.category_bits);
  result.has_mask_bits = vm_lub_bits(source->f_mask_bits, &result.mask_bits);
  if (source->f_group.has) {
    result.has_group = true;
    result.group = source->f_group.v;
  }
  return result;
}
static LubBoxDesc3d vm_lub_to_BoxDesc3d(VmLub_BoxDesc3d *source) {
  LubBoxDesc3d result = {0};
  if (!source)
    return result;
  if (source->f_version.has) {
    result.base.has_version = true;
    result.base.version = source->f_version.v;
  }
  if (source->f_density.has) {
    result.base.has_density = true;
    result.base.density = source->f_density.v;
  }
  if (source->f_friction.has) {
    result.base.has_friction = true;
    result.base.friction = source->f_friction.v;
  }
  if (source->f_restitution.has) {
    result.base.has_restitution = true;
    result.base.restitution = source->f_restitution.v;
  }
  result.base.tag = vm_lub_str(source->f_tag);
  result.base.material_name = vm_lub_str(source->f_material_name);
  if (source->f_material_id.has) {
    result.base.has_material_id = true;
    result.base.material_id = source->f_material_id.v;
  }
  if (source->f_sensor.has) {
    result.base.has_sensor = true;
    result.base.sensor = source->f_sensor.v;
  }
  if (source->f_contact.has) {
    result.base.has_contact = true;
    result.base.contact = source->f_contact.v;
  }
  if (source->f_hit.has) {
    result.base.has_hit = true;
    result.base.hit = source->f_hit.v;
  }
  if (source->f_sensor_events.has) {
    result.base.has_sensor_events = true;
    result.base.sensor_events = source->f_sensor_events.v;
  }
  if (source->f_pre_solve.has) {
    result.base.has_pre_solve = true;
    result.base.pre_solve = source->f_pre_solve.v;
  }
  if (source->f_filter) {
    result.base.has_filter = true;
    result.base.filter = vm_lub_to_FilterDesc3d(source->f_filter);
  }
  result.hx = source->f_hx;
  result.hy = source->f_hy;
  result.hz = source->f_hz;
  if (source->f_offset) {
    result.has_offset = true;
    result.offset = vm_lub_to_Vec3d(source->f_offset);
  }
  if (source->f_quat) {
    result.has_quat = true;
    result.quat = vm_lub_to_Quat3d(source->f_quat);
  }
  return result;
}
static LubCapsuleDesc3d vm_lub_to_CapsuleDesc3d(VmLub_CapsuleDesc3d *source) {
  LubCapsuleDesc3d result = {0};
  if (!source)
    return result;
  if (source->f_version.has) {
    result.base.has_version = true;
    result.base.version = source->f_version.v;
  }
  if (source->f_density.has) {
    result.base.has_density = true;
    result.base.density = source->f_density.v;
  }
  if (source->f_friction.has) {
    result.base.has_friction = true;
    result.base.friction = source->f_friction.v;
  }
  if (source->f_restitution.has) {
    result.base.has_restitution = true;
    result.base.restitution = source->f_restitution.v;
  }
  result.base.tag = vm_lub_str(source->f_tag);
  result.base.material_name = vm_lub_str(source->f_material_name);
  if (source->f_material_id.has) {
    result.base.has_material_id = true;
    result.base.material_id = source->f_material_id.v;
  }
  if (source->f_sensor.has) {
    result.base.has_sensor = true;
    result.base.sensor = source->f_sensor.v;
  }
  if (source->f_contact.has) {
    result.base.has_contact = true;
    result.base.contact = source->f_contact.v;
  }
  if (source->f_hit.has) {
    result.base.has_hit = true;
    result.base.hit = source->f_hit.v;
  }
  if (source->f_sensor_events.has) {
    result.base.has_sensor_events = true;
    result.base.sensor_events = source->f_sensor_events.v;
  }
  if (source->f_pre_solve.has) {
    result.base.has_pre_solve = true;
    result.base.pre_solve = source->f_pre_solve.v;
  }
  if (source->f_filter) {
    result.base.has_filter = true;
    result.base.filter = vm_lub_to_FilterDesc3d(source->f_filter);
  }
  result.a = vm_lub_to_Vec3d(source->f_a);
  result.b = vm_lub_to_Vec3d(source->f_b);
  result.r = source->f_r;
  return result;
}
static LubCylinderDesc3d
vm_lub_to_CylinderDesc3d(VmLub_CylinderDesc3d *source) {
  LubCylinderDesc3d result = {0};
  if (!source)
    return result;
  if (source->f_version.has) {
    result.base.has_version = true;
    result.base.version = source->f_version.v;
  }
  if (source->f_density.has) {
    result.base.has_density = true;
    result.base.density = source->f_density.v;
  }
  if (source->f_friction.has) {
    result.base.has_friction = true;
    result.base.friction = source->f_friction.v;
  }
  if (source->f_restitution.has) {
    result.base.has_restitution = true;
    result.base.restitution = source->f_restitution.v;
  }
  result.base.tag = vm_lub_str(source->f_tag);
  result.base.material_name = vm_lub_str(source->f_material_name);
  if (source->f_material_id.has) {
    result.base.has_material_id = true;
    result.base.material_id = source->f_material_id.v;
  }
  if (source->f_sensor.has) {
    result.base.has_sensor = true;
    result.base.sensor = source->f_sensor.v;
  }
  if (source->f_contact.has) {
    result.base.has_contact = true;
    result.base.contact = source->f_contact.v;
  }
  if (source->f_hit.has) {
    result.base.has_hit = true;
    result.base.hit = source->f_hit.v;
  }
  if (source->f_sensor_events.has) {
    result.base.has_sensor_events = true;
    result.base.sensor_events = source->f_sensor_events.v;
  }
  if (source->f_pre_solve.has) {
    result.base.has_pre_solve = true;
    result.base.pre_solve = source->f_pre_solve.v;
  }
  if (source->f_filter) {
    result.base.has_filter = true;
    result.base.filter = vm_lub_to_FilterDesc3d(source->f_filter);
  }
  result.height = source->f_height;
  result.radius = source->f_radius;
  if (source->f_sides.has) {
    result.has_sides = true;
    result.sides = source->f_sides.v;
  }
  if (source->f_y_offset.has) {
    result.has_y_offset = true;
    result.y_offset = source->f_y_offset.v;
  }
  return result;
}
static LubConeDesc3d vm_lub_to_ConeDesc3d(VmLub_ConeDesc3d *source) {
  LubConeDesc3d result = {0};
  if (!source)
    return result;
  if (source->f_version.has) {
    result.base.has_version = true;
    result.base.version = source->f_version.v;
  }
  if (source->f_density.has) {
    result.base.has_density = true;
    result.base.density = source->f_density.v;
  }
  if (source->f_friction.has) {
    result.base.has_friction = true;
    result.base.friction = source->f_friction.v;
  }
  if (source->f_restitution.has) {
    result.base.has_restitution = true;
    result.base.restitution = source->f_restitution.v;
  }
  result.base.tag = vm_lub_str(source->f_tag);
  result.base.material_name = vm_lub_str(source->f_material_name);
  if (source->f_material_id.has) {
    result.base.has_material_id = true;
    result.base.material_id = source->f_material_id.v;
  }
  if (source->f_sensor.has) {
    result.base.has_sensor = true;
    result.base.sensor = source->f_sensor.v;
  }
  if (source->f_contact.has) {
    result.base.has_contact = true;
    result.base.contact = source->f_contact.v;
  }
  if (source->f_hit.has) {
    result.base.has_hit = true;
    result.base.hit = source->f_hit.v;
  }
  if (source->f_sensor_events.has) {
    result.base.has_sensor_events = true;
    result.base.sensor_events = source->f_sensor_events.v;
  }
  if (source->f_pre_solve.has) {
    result.base.has_pre_solve = true;
    result.base.pre_solve = source->f_pre_solve.v;
  }
  if (source->f_filter) {
    result.base.has_filter = true;
    result.base.filter = vm_lub_to_FilterDesc3d(source->f_filter);
  }
  result.height = source->f_height;
  result.radius1 = source->f_radius1;
  if (source->f_radius2.has) {
    result.has_radius2 = true;
    result.radius2 = source->f_radius2.v;
  }
  if (source->f_slices.has) {
    result.has_slices = true;
    result.slices = source->f_slices.v;
  }
  return result;
}
static LubHullDesc3d vm_lub_to_HullDesc3d(VmLub_HullDesc3d *source) {
  LubHullDesc3d result = {0};
  if (!source)
    return result;
  if (source->f_version.has) {
    result.base.has_version = true;
    result.base.version = source->f_version.v;
  }
  if (source->f_density.has) {
    result.base.has_density = true;
    result.base.density = source->f_density.v;
  }
  if (source->f_friction.has) {
    result.base.has_friction = true;
    result.base.friction = source->f_friction.v;
  }
  if (source->f_restitution.has) {
    result.base.has_restitution = true;
    result.base.restitution = source->f_restitution.v;
  }
  result.base.tag = vm_lub_str(source->f_tag);
  result.base.material_name = vm_lub_str(source->f_material_name);
  if (source->f_material_id.has) {
    result.base.has_material_id = true;
    result.base.material_id = source->f_material_id.v;
  }
  if (source->f_sensor.has) {
    result.base.has_sensor = true;
    result.base.sensor = source->f_sensor.v;
  }
  if (source->f_contact.has) {
    result.base.has_contact = true;
    result.base.contact = source->f_contact.v;
  }
  if (source->f_hit.has) {
    result.base.has_hit = true;
    result.base.hit = source->f_hit.v;
  }
  if (source->f_sensor_events.has) {
    result.base.has_sensor_events = true;
    result.base.sensor_events = source->f_sensor_events.v;
  }
  if (source->f_pre_solve.has) {
    result.base.has_pre_solve = true;
    result.base.pre_solve = source->f_pre_solve.v;
  }
  if (source->f_filter) {
    result.base.has_filter = true;
    result.base.filter = vm_lub_to_FilterDesc3d(source->f_filter);
  }
  if (source->f_points) {
    result.points_count = tvm_list_count(source->f_points);
    result.points = tvm_list_data(source->f_points);
  }
  if (source->f_max_vertices.has) {
    result.has_max_vertices = true;
    result.max_vertices = source->f_max_vertices.v;
  }
  return result;
}
static LubMeshDesc3d vm_lub_to_MeshDesc3d(VmLub_MeshDesc3d *source) {
  LubMeshDesc3d result = {0};
  if (!source)
    return result;
  if (source->f_version.has) {
    result.base.has_version = true;
    result.base.version = source->f_version.v;
  }
  if (source->f_density.has) {
    result.base.has_density = true;
    result.base.density = source->f_density.v;
  }
  if (source->f_friction.has) {
    result.base.has_friction = true;
    result.base.friction = source->f_friction.v;
  }
  if (source->f_restitution.has) {
    result.base.has_restitution = true;
    result.base.restitution = source->f_restitution.v;
  }
  result.base.tag = vm_lub_str(source->f_tag);
  result.base.material_name = vm_lub_str(source->f_material_name);
  if (source->f_material_id.has) {
    result.base.has_material_id = true;
    result.base.material_id = source->f_material_id.v;
  }
  if (source->f_sensor.has) {
    result.base.has_sensor = true;
    result.base.sensor = source->f_sensor.v;
  }
  if (source->f_contact.has) {
    result.base.has_contact = true;
    result.base.contact = source->f_contact.v;
  }
  if (source->f_hit.has) {
    result.base.has_hit = true;
    result.base.hit = source->f_hit.v;
  }
  if (source->f_sensor_events.has) {
    result.base.has_sensor_events = true;
    result.base.sensor_events = source->f_sensor_events.v;
  }
  if (source->f_pre_solve.has) {
    result.base.has_pre_solve = true;
    result.base.pre_solve = source->f_pre_solve.v;
  }
  if (source->f_filter) {
    result.base.has_filter = true;
    result.base.filter = vm_lub_to_FilterDesc3d(source->f_filter);
  }
  if (source->f_positions) {
    result.positions_count = tvm_list_count(source->f_positions);
    result.positions = tvm_list_data(source->f_positions);
  }
  if (source->f_indices) {
    result.indices_count = tvm_list_count(source->f_indices);
    result.indices = tvm_list_data(source->f_indices);
  }
  if (source->f_scale) {
    result.has_scale = true;
    result.scale = vm_lub_to_Vec3d(source->f_scale);
  }
  if (source->f_weld_vertices.has) {
    result.has_weld_vertices = true;
    result.weld_vertices = source->f_weld_vertices.v;
  }
  if (source->f_weld_tolerance.has) {
    result.has_weld_tolerance = true;
    result.weld_tolerance = source->f_weld_tolerance.v;
  }
  if (source->f_use_median_split.has) {
    result.has_use_median_split = true;
    result.use_median_split = source->f_use_median_split.v;
  }
  if (source->f_identify_edges.has) {
    result.has_identify_edges = true;
    result.identify_edges = source->f_identify_edges.v;
  }
  if (source->f_materials) {
    result.materials = vm_lub_to_SurfaceMaterial3d_list(
        source->f_materials, &result.materials_count);
  }
  if (source->f_material_indices) {
    result.material_indices_count = tvm_list_count(source->f_material_indices);
    result.material_indices = tvm_list_data(source->f_material_indices);
  }
  return result;
}
static LubSurfaceMaterial3d
vm_lub_to_SurfaceMaterial3d(VmLub_SurfaceMaterial3d *source) {
  LubSurfaceMaterial3d result = {0};
  if (!source)
    return result;
  if (source->f_friction.has) {
    result.has_friction = true;
    result.friction = source->f_friction.v;
  }
  if (source->f_restitution.has) {
    result.has_restitution = true;
    result.restitution = source->f_restitution.v;
  }
  if (source->f_material_id.has) {
    result.has_material_id = true;
    result.material_id = source->f_material_id.v;
  }
  return result;
}
static LubHeightFieldDesc3d
vm_lub_to_HeightFieldDesc3d(VmLub_HeightFieldDesc3d *source) {
  LubHeightFieldDesc3d result = {0};
  if (!source)
    return result;
  if (source->f_version.has) {
    result.base.has_version = true;
    result.base.version = source->f_version.v;
  }
  if (source->f_density.has) {
    result.base.has_density = true;
    result.base.density = source->f_density.v;
  }
  if (source->f_friction.has) {
    result.base.has_friction = true;
    result.base.friction = source->f_friction.v;
  }
  if (source->f_restitution.has) {
    result.base.has_restitution = true;
    result.base.restitution = source->f_restitution.v;
  }
  result.base.tag = vm_lub_str(source->f_tag);
  result.base.material_name = vm_lub_str(source->f_material_name);
  if (source->f_material_id.has) {
    result.base.has_material_id = true;
    result.base.material_id = source->f_material_id.v;
  }
  if (source->f_sensor.has) {
    result.base.has_sensor = true;
    result.base.sensor = source->f_sensor.v;
  }
  if (source->f_contact.has) {
    result.base.has_contact = true;
    result.base.contact = source->f_contact.v;
  }
  if (source->f_hit.has) {
    result.base.has_hit = true;
    result.base.hit = source->f_hit.v;
  }
  if (source->f_sensor_events.has) {
    result.base.has_sensor_events = true;
    result.base.sensor_events = source->f_sensor_events.v;
  }
  if (source->f_pre_solve.has) {
    result.base.has_pre_solve = true;
    result.base.pre_solve = source->f_pre_solve.v;
  }
  if (source->f_filter) {
    result.base.has_filter = true;
    result.base.filter = vm_lub_to_FilterDesc3d(source->f_filter);
  }
  if (source->f_heights) {
    result.heights_count = tvm_list_count(source->f_heights);
    result.heights = tvm_list_data(source->f_heights);
  }
  result.x_count = source->f_x_count;
  result.z_count = source->f_z_count;
  if (source->f_cell_width.has) {
    result.has_cell_width = true;
    result.cell_width = source->f_cell_width.v;
  }
  if (source->f_scale) {
    result.has_scale = true;
    result.scale = vm_lub_to_Vec3d(source->f_scale);
  }
  if (source->f_min_height.has) {
    result.has_min_height = true;
    result.min_height = source->f_min_height.v;
  }
  if (source->f_max_height.has) {
    result.has_max_height = true;
    result.max_height = source->f_max_height.v;
  }
  if (source->f_clockwise_winding.has) {
    result.has_clockwise_winding = true;
    result.clockwise_winding = source->f_clockwise_winding.v;
  }
  return result;
}
static LubCompoundDesc3d
vm_lub_to_CompoundDesc3d(VmLub_CompoundDesc3d *source) {
  LubCompoundDesc3d result = {0};
  if (!source)
    return result;
  if (source->f_version.has) {
    result.base.has_version = true;
    result.base.version = source->f_version.v;
  }
  if (source->f_density.has) {
    result.base.has_density = true;
    result.base.density = source->f_density.v;
  }
  if (source->f_friction.has) {
    result.base.has_friction = true;
    result.base.friction = source->f_friction.v;
  }
  if (source->f_restitution.has) {
    result.base.has_restitution = true;
    result.base.restitution = source->f_restitution.v;
  }
  result.base.tag = vm_lub_str(source->f_tag);
  result.base.material_name = vm_lub_str(source->f_material_name);
  if (source->f_material_id.has) {
    result.base.has_material_id = true;
    result.base.material_id = source->f_material_id.v;
  }
  if (source->f_sensor.has) {
    result.base.has_sensor = true;
    result.base.sensor = source->f_sensor.v;
  }
  if (source->f_contact.has) {
    result.base.has_contact = true;
    result.base.contact = source->f_contact.v;
  }
  if (source->f_hit.has) {
    result.base.has_hit = true;
    result.base.hit = source->f_hit.v;
  }
  if (source->f_sensor_events.has) {
    result.base.has_sensor_events = true;
    result.base.sensor_events = source->f_sensor_events.v;
  }
  if (source->f_pre_solve.has) {
    result.base.has_pre_solve = true;
    result.base.pre_solve = source->f_pre_solve.v;
  }
  if (source->f_filter) {
    result.base.has_filter = true;
    result.base.filter = vm_lub_to_FilterDesc3d(source->f_filter);
  }
  if (source->f_children) {
    result.children = vm_lub_to_CompoundChild3d_list(source->f_children,
                                                     &result.children_count);
  }
  return result;
}
static LubCompoundChild3d
vm_lub_to_CompoundChild3d(VmLub_CompoundChild3d *source) {
  LubCompoundChild3d result = {0};
  if (!source)
    return result;
  if (source->f_pose) {
    result.has_pose = true;
    result.pose = vm_lub_to_FrameDesc3d(source->f_pose);
  }
  if (source->f_friction.has) {
    result.has_friction = true;
    result.friction = source->f_friction.v;
  }
  if (source->f_restitution.has) {
    result.has_restitution = true;
    result.restitution = source->f_restitution.v;
  }
  if (source->f_material_id.has) {
    result.has_material_id = true;
    result.material_id = source->f_material_id.v;
  }
  if (source->f_sphere) {
    result.has_sphere = true;
    result.sphere = vm_lub_to_CompoundSphere3d(source->f_sphere);
  }
  if (source->f_box) {
    result.has_box = true;
    result.box = vm_lub_to_CompoundBox3d(source->f_box);
  }
  if (source->f_capsule) {
    result.has_capsule = true;
    result.capsule = vm_lub_to_CompoundCapsule3d(source->f_capsule);
  }
  return result;
}
static LubFrameDesc3d vm_lub_to_FrameDesc3d(VmLub_FrameDesc3d *source) {
  LubFrameDesc3d result = {0};
  if (!source)
    return result;
  if (source->f_x.has) {
    result.has_x = true;
    result.x = source->f_x.v;
  }
  if (source->f_y.has) {
    result.has_y = true;
    result.y = source->f_y.v;
  }
  if (source->f_z.has) {
    result.has_z = true;
    result.z = source->f_z.v;
  }
  if (source->f_quat) {
    result.has_quat = true;
    result.quat = vm_lub_to_Quat3d(source->f_quat);
  }
  if (source->f_euler) {
    result.has_euler = true;
    result.euler = vm_lub_to_Vec3d(source->f_euler);
  }
  return result;
}
static LubCompoundSphere3d
vm_lub_to_CompoundSphere3d(VmLub_CompoundSphere3d *source) {
  LubCompoundSphere3d result = {0};
  if (!source)
    return result;
  result.r = source->f_r;
  if (source->f_center) {
    result.has_center = true;
    result.center = vm_lub_to_Vec3d(source->f_center);
  }
  return result;
}
static LubCompoundBox3d vm_lub_to_CompoundBox3d(VmLub_CompoundBox3d *source) {
  LubCompoundBox3d result = {0};
  if (!source)
    return result;
  result.hx = source->f_hx;
  result.hy = source->f_hy;
  result.hz = source->f_hz;
  return result;
}
static LubCompoundCapsule3d
vm_lub_to_CompoundCapsule3d(VmLub_CompoundCapsule3d *source) {
  LubCompoundCapsule3d result = {0};
  if (!source)
    return result;
  result.a = vm_lub_to_Vec3d(source->f_a);
  result.b = vm_lub_to_Vec3d(source->f_b);
  result.r = source->f_r;
  return result;
}
static LubJointDesc3d vm_lub_to_JointDesc3d(VmLub_JointDesc3d *source) {
  LubJointDesc3d result = {0};
  if (!source)
    return result;
  if (source->f_version.has) {
    result.has_version = true;
    result.version = source->f_version.v;
  }
  if (source->f_type.has) {
    result.has_type = true;
    result.type = source->f_type.v;
  }
  result.body_a =
      (source->f_body_a ? (int32_t)source->f_body_a->host_value : 0);
  result.body_b =
      (source->f_body_b ? (int32_t)source->f_body_b->host_value : 0);
  if (source->f_anchor_a) {
    result.has_anchor_a = true;
    result.anchor_a = vm_lub_to_Vec3d(source->f_anchor_a);
  }
  if (source->f_anchor_b) {
    result.has_anchor_b = true;
    result.anchor_b = vm_lub_to_Vec3d(source->f_anchor_b);
  }
  if (source->f_axis) {
    result.has_axis = true;
    result.axis = vm_lub_to_Vec3d(source->f_axis);
  }
  if (source->f_frame_a) {
    result.has_frame_a = true;
    result.frame_a = vm_lub_to_FrameDesc3d(source->f_frame_a);
  }
  if (source->f_frame_b) {
    result.has_frame_b = true;
    result.frame_b = vm_lub_to_FrameDesc3d(source->f_frame_b);
  }
  if (source->f_collide_connected.has) {
    result.has_collide_connected = true;
    result.collide_connected = source->f_collide_connected.v;
  }
  if (source->f_force_threshold.has) {
    result.has_force_threshold = true;
    result.force_threshold = source->f_force_threshold.v;
  }
  if (source->f_torque_threshold.has) {
    result.has_torque_threshold = true;
    result.torque_threshold = source->f_torque_threshold.v;
  }
  if (source->f_constraint_hertz.has) {
    result.has_constraint_hertz = true;
    result.constraint_hertz = source->f_constraint_hertz.v;
  }
  if (source->f_constraint_damping_ratio.has) {
    result.has_constraint_damping_ratio = true;
    result.constraint_damping_ratio = source->f_constraint_damping_ratio.v;
  }
  if (source->f_length.has) {
    result.has_length = true;
    result.length = source->f_length.v;
  }
  if (source->f_min_length.has) {
    result.has_min_length = true;
    result.min_length = source->f_min_length.v;
  }
  if (source->f_max_length.has) {
    result.has_max_length = true;
    result.max_length = source->f_max_length.v;
  }
  if (source->f_lower.has) {
    result.has_lower = true;
    result.lower = source->f_lower.v;
  }
  if (source->f_upper.has) {
    result.has_upper = true;
    result.upper = source->f_upper.v;
  }
  if (source->f_hertz.has) {
    result.has_hertz = true;
    result.hertz = source->f_hertz.v;
  }
  if (source->f_damping_ratio.has) {
    result.has_damping_ratio = true;
    result.damping_ratio = source->f_damping_ratio.v;
  }
  if (source->f_linear_hertz.has) {
    result.has_linear_hertz = true;
    result.linear_hertz = source->f_linear_hertz.v;
  }
  if (source->f_angular_hertz.has) {
    result.has_angular_hertz = true;
    result.angular_hertz = source->f_angular_hertz.v;
  }
  if (source->f_linear_damping_ratio.has) {
    result.has_linear_damping_ratio = true;
    result.linear_damping_ratio = source->f_linear_damping_ratio.v;
  }
  if (source->f_angular_damping_ratio.has) {
    result.has_angular_damping_ratio = true;
    result.angular_damping_ratio = source->f_angular_damping_ratio.v;
  }
  if (source->f_max_force.has) {
    result.has_max_force = true;
    result.max_force = source->f_max_force.v;
  }
  if (source->f_max_torque.has) {
    result.has_max_torque = true;
    result.max_torque = source->f_max_torque.v;
  }
  if (source->f_max_velocity_force.has) {
    result.has_max_velocity_force = true;
    result.max_velocity_force = source->f_max_velocity_force.v;
  }
  if (source->f_max_velocity_torque.has) {
    result.has_max_velocity_torque = true;
    result.max_velocity_torque = source->f_max_velocity_torque.v;
  }
  if (source->f_max_spring_force.has) {
    result.has_max_spring_force = true;
    result.max_spring_force = source->f_max_spring_force.v;
  }
  if (source->f_max_spring_torque.has) {
    result.has_max_spring_torque = true;
    result.max_spring_torque = source->f_max_spring_torque.v;
  }
  if (source->f_motor_speed.has) {
    result.has_motor_speed = true;
    result.motor_speed = source->f_motor_speed.v;
  }
  if (source->f_target_angle.has) {
    result.has_target_angle = true;
    result.target_angle = source->f_target_angle.v;
  }
  if (source->f_target_translation.has) {
    result.has_target_translation = true;
    result.target_translation = source->f_target_translation.v;
  }
  if (source->f_target_rotation) {
    result.has_target_rotation = true;
    result.target_rotation = vm_lub_to_Quat3d(source->f_target_rotation);
  }
  if (source->f_linear_velocity) {
    result.has_linear_velocity = true;
    result.linear_velocity = vm_lub_to_Vec3d(source->f_linear_velocity);
  }
  if (source->f_angular_velocity) {
    result.has_angular_velocity = true;
    result.angular_velocity = vm_lub_to_Vec3d(source->f_angular_velocity);
  }
  if (source->f_motor_velocity) {
    result.has_motor_velocity = true;
    result.motor_velocity = vm_lub_to_Vec3d(source->f_motor_velocity);
  }
  if (source->f_enable_spring.has) {
    result.has_enable_spring = true;
    result.enable_spring = source->f_enable_spring.v;
  }
  if (source->f_enable_limit.has) {
    result.has_enable_limit = true;
    result.enable_limit = source->f_enable_limit.v;
  }
  if (source->f_enable_motor.has) {
    result.has_enable_motor = true;
    result.enable_motor = source->f_enable_motor.v;
  }
  if (source->f_cone_angle.has) {
    result.has_cone_angle = true;
    result.cone_angle = source->f_cone_angle.v;
  }
  if (source->f_enable_cone_limit.has) {
    result.has_enable_cone_limit = true;
    result.enable_cone_limit = source->f_enable_cone_limit.v;
  }
  if (source->f_enable_twist_limit.has) {
    result.has_enable_twist_limit = true;
    result.enable_twist_limit = source->f_enable_twist_limit.v;
  }
  if (source->f_lower_twist_angle.has) {
    result.has_lower_twist_angle = true;
    result.lower_twist_angle = source->f_lower_twist_angle.v;
  }
  if (source->f_upper_twist_angle.has) {
    result.has_upper_twist_angle = true;
    result.upper_twist_angle = source->f_upper_twist_angle.v;
  }
  if (source->f_spring) {
    result.has_spring = true;
    result.spring = vm_lub_to_JointSpringDesc3d(source->f_spring);
  }
  if (source->f_limit) {
    result.has_limit = true;
    result.limit = vm_lub_to_JointLimitDesc3d(source->f_limit);
  }
  if (source->f_motor) {
    result.has_motor = true;
    result.motor = vm_lub_to_JointMotorDesc3d(source->f_motor);
  }
  return result;
}
static LubJointSpringDesc3d
vm_lub_to_JointSpringDesc3d(VmLub_JointSpringDesc3d *source) {
  LubJointSpringDesc3d result = {0};
  if (!source)
    return result;
  if (source->f_enabled.has) {
    result.has_enabled = true;
    result.enabled = source->f_enabled.v;
  }
  if (source->f_hertz.has) {
    result.has_hertz = true;
    result.hertz = source->f_hertz.v;
  }
  if (source->f_damping_ratio.has) {
    result.has_damping_ratio = true;
    result.damping_ratio = source->f_damping_ratio.v;
  }
  if (source->f_linear_hertz.has) {
    result.has_linear_hertz = true;
    result.linear_hertz = source->f_linear_hertz.v;
  }
  if (source->f_linear_damping_ratio.has) {
    result.has_linear_damping_ratio = true;
    result.linear_damping_ratio = source->f_linear_damping_ratio.v;
  }
  if (source->f_angular_hertz.has) {
    result.has_angular_hertz = true;
    result.angular_hertz = source->f_angular_hertz.v;
  }
  if (source->f_angular_damping_ratio.has) {
    result.has_angular_damping_ratio = true;
    result.angular_damping_ratio = source->f_angular_damping_ratio.v;
  }
  if (source->f_max_torque.has) {
    result.has_max_torque = true;
    result.max_torque = source->f_max_torque.v;
  }
  return result;
}
static LubJointLimitDesc3d
vm_lub_to_JointLimitDesc3d(VmLub_JointLimitDesc3d *source) {
  LubJointLimitDesc3d result = {0};
  if (!source)
    return result;
  if (source->f_enabled.has) {
    result.has_enabled = true;
    result.enabled = source->f_enabled.v;
  }
  if (source->f_lower.has) {
    result.has_lower = true;
    result.lower = source->f_lower.v;
  }
  if (source->f_upper.has) {
    result.has_upper = true;
    result.upper = source->f_upper.v;
  }
  if (source->f_min_length.has) {
    result.has_min_length = true;
    result.min_length = source->f_min_length.v;
  }
  if (source->f_max_length.has) {
    result.has_max_length = true;
    result.max_length = source->f_max_length.v;
  }
  if (source->f_cone_angle.has) {
    result.has_cone_angle = true;
    result.cone_angle = source->f_cone_angle.v;
  }
  if (source->f_lower_twist_angle.has) {
    result.has_lower_twist_angle = true;
    result.lower_twist_angle = source->f_lower_twist_angle.v;
  }
  if (source->f_upper_twist_angle.has) {
    result.has_upper_twist_angle = true;
    result.upper_twist_angle = source->f_upper_twist_angle.v;
  }
  return result;
}
static LubJointMotorDesc3d
vm_lub_to_JointMotorDesc3d(VmLub_JointMotorDesc3d *source) {
  LubJointMotorDesc3d result = {0};
  if (!source)
    return result;
  if (source->f_enabled.has) {
    result.has_enabled = true;
    result.enabled = source->f_enabled.v;
  }
  if (source->f_speed.has) {
    result.has_speed = true;
    result.speed = source->f_speed.v;
  }
  if (source->f_max_force.has) {
    result.has_max_force = true;
    result.max_force = source->f_max_force.v;
  }
  if (source->f_max_torque.has) {
    result.has_max_torque = true;
    result.max_torque = source->f_max_torque.v;
  }
  if (source->f_velocity) {
    result.has_velocity = true;
    result.velocity = vm_lub_to_Vec3d(source->f_velocity);
  }
  if (source->f_linear_velocity) {
    result.has_linear_velocity = true;
    result.linear_velocity = vm_lub_to_Vec3d(source->f_linear_velocity);
  }
  if (source->f_angular_velocity) {
    result.has_angular_velocity = true;
    result.angular_velocity = vm_lub_to_Vec3d(source->f_angular_velocity);
  }
  if (source->f_max_velocity_force.has) {
    result.has_max_velocity_force = true;
    result.max_velocity_force = source->f_max_velocity_force.v;
  }
  if (source->f_max_velocity_torque.has) {
    result.has_max_velocity_torque = true;
    result.max_velocity_torque = source->f_max_velocity_torque.v;
  }
  return result;
}
static LubJointTargetDesc3d
vm_lub_to_JointTargetDesc3d(VmLub_JointTargetDesc3d *source) {
  LubJointTargetDesc3d result = {0};
  if (!source)
    return result;
  if (source->f_translation.has) {
    result.has_translation = true;
    result.translation = source->f_translation.v;
  }
  if (source->f_angle.has) {
    result.has_angle = true;
    result.angle = source->f_angle.v;
  }
  if (source->f_steering_angle.has) {
    result.has_steering_angle = true;
    result.steering_angle = source->f_steering_angle.v;
  }
  if (source->f_quat) {
    result.has_quat = true;
    result.quat = vm_lub_to_Quat3d(source->f_quat);
  }
  if (source->f_euler) {
    result.has_euler = true;
    result.euler = vm_lub_to_Vec3d(source->f_euler);
  }
  if (source->f_linear_velocity) {
    result.has_linear_velocity = true;
    result.linear_velocity = vm_lub_to_Vec3d(source->f_linear_velocity);
  }
  if (source->f_angular_velocity) {
    result.has_angular_velocity = true;
    result.angular_velocity = vm_lub_to_Vec3d(source->f_angular_velocity);
  }
  return result;
}
static LubMoverDesc3d vm_lub_to_MoverDesc3d(VmLub_MoverDesc3d *source) {
  LubMoverDesc3d result = {0};
  if (!source)
    return result;
  result.a = vm_lub_to_Vec3d(source->f_a);
  result.b = vm_lub_to_Vec3d(source->f_b);
  result.r = source->f_r;
  if (source->f_dx.has) {
    result.has_dx = true;
    result.dx = source->f_dx.v;
  }
  if (source->f_dy.has) {
    result.has_dy = true;
    result.dy = source->f_dy.v;
  }
  if (source->f_dz.has) {
    result.has_dz = true;
    result.dz = source->f_dz.v;
  }
  if (source->f_max_fraction.has) {
    result.has_max_fraction = true;
    result.max_fraction = source->f_max_fraction.v;
  }
  if (source->f_filter) {
    result.has_filter = true;
    result.filter = vm_lub_to_FilterDesc3d(source->f_filter);
  }
  return result;
}
static LubCommandOpts3d vm_lub_to_CommandOpts3d(VmLub_CommandOpts3d *source) {
  LubCommandOpts3d result = {0};
  if (!source)
    return result;
  if (source->f_wake.has) {
    result.has_wake = true;
    result.wake = source->f_wake.v;
  }
  if (source->f_point) {
    result.has_point = true;
    result.point = vm_lub_to_Vec3d(source->f_point);
  }
  return result;
}
static LubVelocityDesc3d
vm_lub_to_VelocityDesc3d(VmLub_VelocityDesc3d *source) {
  LubVelocityDesc3d result = {0};
  if (!source)
    return result;
  if (source->f_vx.has) {
    result.has_vx = true;
    result.vx = source->f_vx.v;
  }
  if (source->f_vy.has) {
    result.has_vy = true;
    result.vy = source->f_vy.v;
  }
  if (source->f_vz.has) {
    result.has_vz = true;
    result.vz = source->f_vz.v;
  }
  if (source->f_wx.has) {
    result.has_wx = true;
    result.wx = source->f_wx.v;
  }
  if (source->f_wy.has) {
    result.has_wy = true;
    result.wy = source->f_wy.v;
  }
  if (source->f_wz.has) {
    result.has_wz = true;
    result.wz = source->f_wz.v;
  }
  return result;
}
static LubPoseDesc3d vm_lub_to_PoseDesc3d(VmLub_PoseDesc3d *source) {
  LubPoseDesc3d result = {0};
  if (!source)
    return result;
  if (source->f_x.has) {
    result.has_x = true;
    result.x = source->f_x.v;
  }
  if (source->f_y.has) {
    result.has_y = true;
    result.y = source->f_y.v;
  }
  if (source->f_z.has) {
    result.has_z = true;
    result.z = source->f_z.v;
  }
  if (source->f_quat) {
    result.has_quat = true;
    result.quat = vm_lub_to_Quat3d(source->f_quat);
  }
  if (source->f_euler) {
    result.has_euler = true;
    result.euler = vm_lub_to_Vec3d(source->f_euler);
  }
  return result;
}
static LubTargetDesc3d vm_lub_to_TargetDesc3d(VmLub_TargetDesc3d *source) {
  LubTargetDesc3d result = {0};
  if (!source)
    return result;
  if (source->f_x.has) {
    result.has_x = true;
    result.x = source->f_x.v;
  }
  if (source->f_y.has) {
    result.has_y = true;
    result.y = source->f_y.v;
  }
  if (source->f_z.has) {
    result.has_z = true;
    result.z = source->f_z.v;
  }
  if (source->f_quat) {
    result.has_quat = true;
    result.quat = vm_lub_to_Quat3d(source->f_quat);
  }
  if (source->f_euler) {
    result.has_euler = true;
    result.euler = vm_lub_to_Vec3d(source->f_euler);
  }
  if (source->f_time_step.has) {
    result.has_time_step = true;
    result.time_step = source->f_time_step.v;
  }
  if (source->f_wake.has) {
    result.has_wake = true;
    result.wake = source->f_wake.v;
  }
  return result;
}
static LubRaycastDesc3d vm_lub_to_RaycastDesc3d(VmLub_RaycastDesc3d *source) {
  LubRaycastDesc3d result = {0};
  if (!source)
    return result;
  if (source->f_x.has) {
    result.has_x = true;
    result.x = source->f_x.v;
  }
  if (source->f_y.has) {
    result.has_y = true;
    result.y = source->f_y.v;
  }
  if (source->f_z.has) {
    result.has_z = true;
    result.z = source->f_z.v;
  }
  if (source->f_dx.has) {
    result.has_dx = true;
    result.dx = source->f_dx.v;
  }
  if (source->f_dy.has) {
    result.has_dy = true;
    result.dy = source->f_dy.v;
  }
  if (source->f_dz.has) {
    result.has_dz = true;
    result.dz = source->f_dz.v;
  }
  if (source->f_max_fraction.has) {
    result.has_max_fraction = true;
    result.max_fraction = source->f_max_fraction.v;
  }
  if (source->f_filter) {
    result.has_filter = true;
    result.filter = vm_lub_to_FilterDesc3d(source->f_filter);
  }
  return result;
}
static LubAabbDesc3d vm_lub_to_AabbDesc3d(VmLub_AabbDesc3d *source) {
  LubAabbDesc3d result = {0};
  if (!source)
    return result;
  result.min_x = source->f_min_x;
  result.min_y = source->f_min_y;
  result.min_z = source->f_min_z;
  result.max_x = source->f_max_x;
  result.max_y = source->f_max_y;
  result.max_z = source->f_max_z;
  if (source->f_filter) {
    result.has_filter = true;
    result.filter = vm_lub_to_FilterDesc3d(source->f_filter);
  }
  return result;
}
static LubShapeProxyDesc3d
vm_lub_to_ShapeProxyDesc3d(VmLub_ShapeProxyDesc3d *source) {
  LubShapeProxyDesc3d result = {0};
  if (!source)
    return result;
  if (source->f_sphere) {
    result.has_sphere = true;
    result.sphere = vm_lub_to_SphereProxy3d(source->f_sphere);
  }
  if (source->f_box) {
    result.has_box = true;
    result.box = vm_lub_to_BoxProxy3d(source->f_box);
  }
  if (source->f_capsule) {
    result.has_capsule = true;
    result.capsule = vm_lub_to_CapsuleProxy3d(source->f_capsule);
  }
  if (source->f_dx.has) {
    result.has_dx = true;
    result.dx = source->f_dx.v;
  }
  if (source->f_dy.has) {
    result.has_dy = true;
    result.dy = source->f_dy.v;
  }
  if (source->f_dz.has) {
    result.has_dz = true;
    result.dz = source->f_dz.v;
  }
  if (source->f_max_fraction.has) {
    result.has_max_fraction = true;
    result.max_fraction = source->f_max_fraction.v;
  }
  if (source->f_filter) {
    result.has_filter = true;
    result.filter = vm_lub_to_FilterDesc3d(source->f_filter);
  }
  return result;
}
static LubSphereProxy3d vm_lub_to_SphereProxy3d(VmLub_SphereProxy3d *source) {
  LubSphereProxy3d result = {0};
  if (!source)
    return result;
  result.r = source->f_r;
  if (source->f_center) {
    result.has_center = true;
    result.center = vm_lub_to_Vec3d(source->f_center);
  }
  return result;
}
static LubBoxProxy3d vm_lub_to_BoxProxy3d(VmLub_BoxProxy3d *source) {
  LubBoxProxy3d result = {0};
  if (!source)
    return result;
  result.hx = source->f_hx;
  result.hy = source->f_hy;
  result.hz = source->f_hz;
  if (source->f_radius.has) {
    result.has_radius = true;
    result.radius = source->f_radius.v;
  }
  if (source->f_center) {
    result.has_center = true;
    result.center = vm_lub_to_Vec3d(source->f_center);
  }
  if (source->f_quat) {
    result.has_quat = true;
    result.quat = vm_lub_to_Quat3d(source->f_quat);
  }
  return result;
}
static LubCapsuleProxy3d
vm_lub_to_CapsuleProxy3d(VmLub_CapsuleProxy3d *source) {
  LubCapsuleProxy3d result = {0};
  if (!source)
    return result;
  result.a = vm_lub_to_Vec3d(source->f_a);
  result.b = vm_lub_to_Vec3d(source->f_b);
  result.r = source->f_r;
  return result;
}
static LubMaterialDesc3d
vm_lub_to_MaterialDesc3d(VmLub_MaterialDesc3d *source) {
  LubMaterialDesc3d result = {0};
  if (!source)
    return result;
  if (source->f_density.has) {
    result.has_density = true;
    result.density = source->f_density.v;
  }
  if (source->f_friction.has) {
    result.has_friction = true;
    result.friction = source->f_friction.v;
  }
  if (source->f_restitution.has) {
    result.has_restitution = true;
    result.restitution = source->f_restitution.v;
  }
  result.material_name = vm_lub_str(source->f_material_name);
  if (source->f_material_id.has) {
    result.has_material_id = true;
    result.material_id = source->f_material_id.v;
  }
  return result;
}
static LubShapeEventsDesc3d
vm_lub_to_ShapeEventsDesc3d(VmLub_ShapeEventsDesc3d *source) {
  LubShapeEventsDesc3d result = {0};
  if (!source)
    return result;
  if (source->f_sensor_events.has) {
    result.has_sensor_events = true;
    result.sensor_events = source->f_sensor_events.v;
  }
  if (source->f_contact.has) {
    result.has_contact = true;
    result.contact = source->f_contact.v;
  }
  if (source->f_pre_solve.has) {
    result.has_pre_solve = true;
    result.pre_solve = source->f_pre_solve.v;
  }
  if (source->f_hit.has) {
    result.has_hit = true;
    result.hit = source->f_hit.v;
  }
  return result;
}
static VmLub_XrView *vm_lub_from_XrView(const LubXrView *source) {
  VmLub_XrView *result = vm_lub_new(&vm_lub_types[14], sizeof(*result));
  result->f_target = vm_lub_handle_TextureRef(source->target);
  result->f_width = source->width;
  result->f_height = source->height;
  result->f_position = vm_lub_array(&vm_lub_types[15], 3);
  if (result->f_position)
    memcpy(tvm_array_data(result->f_position), source->position,
           sizeof(source->position));
  result->f_orientation = vm_lub_array(&vm_lub_types[15], 4);
  if (result->f_orientation)
    memcpy(tvm_array_data(result->f_orientation), source->orientation,
           sizeof(source->orientation));
  result->f_view_projection = vm_lub_array(&vm_lub_types[15], 16);
  if (result->f_view_projection)
    memcpy(tvm_array_data(result->f_view_projection), source->view_projection,
           sizeof(source->view_projection));
  return result;
}
static VmLub_XrInput *vm_lub_from_XrInput(const LubXrInput *source) {
  VmLub_XrInput *result = vm_lub_new(&vm_lub_types[16], sizeof(*result));
  result->f_active = source->active;
  result->f_stick_x = source->stick_x;
  result->f_stick_y = source->stick_y;
  result->f_trigger = source->trigger;
  result->f_grip = source->grip;
  result->f_primary = source->primary;
  result->f_secondary = source->secondary;
  result->f_menu = source->menu;
  result->f_stick_click = source->stick_click;
  return result;
}
static VmLub_GltfMesh *vm_lub_from_GltfMesh(const LubGltfMesh *source) {
  VmLub_GltfMesh *result = vm_lub_new(&vm_lub_types[17], sizeof(*result));
  result->f_positions = vm_lub_list(
      &vm_lub_types[18], NULL, source->base.positions,
      source->base.positions_count, sizeof(*source->base.positions));
  result->f_normals =
      vm_lub_list(&vm_lub_types[18], NULL, source->base.normals,
                  source->base.normals_count, sizeof(*source->base.normals));
  result->f_indices =
      vm_lub_list(&vm_lub_types[19], NULL, source->base.indices,
                  source->base.indices_count, sizeof(*source->base.indices));
  result->f_vert_count = source->base.vert_count;
  result->f_index_count = source->base.index_count;
  result->f_uvs =
      (source->base.uvs
           ? vm_lub_list(&vm_lub_types[18], NULL, source->base.uvs,
                         source->base.uvs_count, sizeof(*source->base.uvs))
           : NULL);
  result->f_tangents =
      (source->base.tangents
           ? vm_lub_list(&vm_lub_types[18], NULL, source->base.tangents,
                         source->base.tangents_count,
                         sizeof(*source->base.tangents))
           : NULL);
  result->f_bounds_min =
      (source->base.bounds_min
           ? vm_lub_list(&vm_lub_types[18], NULL, source->base.bounds_min,
                         source->base.bounds_min_count,
                         sizeof(*source->base.bounds_min))
           : NULL);
  result->f_bounds_max =
      (source->base.bounds_max
           ? vm_lub_list(&vm_lub_types[18], NULL, source->base.bounds_max,
                         source->base.bounds_max_count,
                         sizeof(*source->base.bounds_max))
           : NULL);
  result->f_cell = (VmLubOptF32){source->base.has_cell, source->base.cell};
  result->f_colors =
      (source->base.colors
           ? vm_lub_list(&vm_lub_types[18], NULL, source->base.colors,
                         source->base.colors_count,
                         sizeof(*source->base.colors))
           : NULL);
  result->f_metal_rough =
      (source->base.metal_rough
           ? vm_lub_list(&vm_lub_types[18], NULL, source->base.metal_rough,
                         source->base.metal_rough_count,
                         sizeof(*source->base.metal_rough))
           : NULL);
  result->f_joints =
      (source->base.joints
           ? vm_lub_list(&vm_lub_types[19], NULL, source->base.joints,
                         source->base.joints_count,
                         sizeof(*source->base.joints))
           : NULL);
  result->f_weights =
      (source->base.weights
           ? vm_lub_list(&vm_lub_types[18], NULL, source->base.weights,
                         source->base.weights_count,
                         sizeof(*source->base.weights))
           : NULL);
  result->f_bones =
      (source->base.bones ? vm_lub_from_SdfBone_list(NULL, source->base.bones,
                                                     source->base.bones_count)
                          : NULL);
  result->f_primitives = vm_lub_from_GltfPrimitive_list(
      NULL, source->primitives, source->primitives_count);
  result->f_material =
      source->has_material ? vm_lub_from_GltfMaterial(&source->material) : NULL;
  return result;
}
static VmLub_SdfBone *vm_lub_from_SdfBone(const LubSdfBone *source) {
  VmLub_SdfBone *result = vm_lub_new(&vm_lub_types[20], sizeof(*result));
  result->f_name = vm_lub_string(source->name);
  result->f_x = source->x;
  result->f_y = source->y;
  result->f_z = source->z;
  return result;
}
static VmLub_GltfPrimitive *
vm_lub_from_GltfPrimitive(const LubGltfPrimitive *source) {
  VmLub_GltfPrimitive *result = vm_lub_new(&vm_lub_types[21], sizeof(*result));
  result->f_positions = vm_lub_list(
      &vm_lub_types[18], NULL, source->base.positions,
      source->base.positions_count, sizeof(*source->base.positions));
  result->f_normals =
      vm_lub_list(&vm_lub_types[18], NULL, source->base.normals,
                  source->base.normals_count, sizeof(*source->base.normals));
  result->f_indices =
      vm_lub_list(&vm_lub_types[19], NULL, source->base.indices,
                  source->base.indices_count, sizeof(*source->base.indices));
  result->f_vert_count = source->base.vert_count;
  result->f_index_count = source->base.index_count;
  result->f_uvs =
      (source->base.uvs
           ? vm_lub_list(&vm_lub_types[18], NULL, source->base.uvs,
                         source->base.uvs_count, sizeof(*source->base.uvs))
           : NULL);
  result->f_tangents =
      (source->base.tangents
           ? vm_lub_list(&vm_lub_types[18], NULL, source->base.tangents,
                         source->base.tangents_count,
                         sizeof(*source->base.tangents))
           : NULL);
  result->f_bounds_min =
      (source->base.bounds_min
           ? vm_lub_list(&vm_lub_types[18], NULL, source->base.bounds_min,
                         source->base.bounds_min_count,
                         sizeof(*source->base.bounds_min))
           : NULL);
  result->f_bounds_max =
      (source->base.bounds_max
           ? vm_lub_list(&vm_lub_types[18], NULL, source->base.bounds_max,
                         source->base.bounds_max_count,
                         sizeof(*source->base.bounds_max))
           : NULL);
  result->f_cell = (VmLubOptF32){source->base.has_cell, source->base.cell};
  result->f_colors =
      (source->base.colors
           ? vm_lub_list(&vm_lub_types[18], NULL, source->base.colors,
                         source->base.colors_count,
                         sizeof(*source->base.colors))
           : NULL);
  result->f_metal_rough =
      (source->base.metal_rough
           ? vm_lub_list(&vm_lub_types[18], NULL, source->base.metal_rough,
                         source->base.metal_rough_count,
                         sizeof(*source->base.metal_rough))
           : NULL);
  result->f_joints =
      (source->base.joints
           ? vm_lub_list(&vm_lub_types[19], NULL, source->base.joints,
                         source->base.joints_count,
                         sizeof(*source->base.joints))
           : NULL);
  result->f_weights =
      (source->base.weights
           ? vm_lub_list(&vm_lub_types[18], NULL, source->base.weights,
                         source->base.weights_count,
                         sizeof(*source->base.weights))
           : NULL);
  result->f_bones =
      (source->base.bones ? vm_lub_from_SdfBone_list(NULL, source->base.bones,
                                                     source->base.bones_count)
                          : NULL);
  result->f_material_index = source->material_index;
  result->f_material =
      source->has_material ? vm_lub_from_GltfMaterial(&source->material) : NULL;
  return result;
}
static VmLub_GltfMaterial *
vm_lub_from_GltfMaterial(const LubGltfMaterial *source) {
  VmLub_GltfMaterial *result = vm_lub_new(&vm_lub_types[22], sizeof(*result));
  result->f_base_color_factor = vm_lub_list(
      &vm_lub_types[18], NULL, source->base_color_factor,
      source->base_color_factor_count, sizeof(*source->base_color_factor));
  result->f_metallic_factor = source->metallic_factor;
  result->f_roughness_factor = source->roughness_factor;
  result->f_alpha_mode = source->alpha_mode;
  result->f_alpha_cutoff = source->alpha_cutoff;
  result->f_double_sided = source->double_sided;
  result->f_normal_scale = source->normal_scale;
  result->f_base_color_path = vm_lub_string(source->base_color_path);
  result->f_metallic_roughness_path =
      vm_lub_string(source->metallic_roughness_path);
  result->f_normal_path = vm_lub_string(source->normal_path);
  result->f_name = vm_lub_string(source->name);
  return result;
}
static VmLub_MeshData *vm_lub_from_MeshData(const LubMeshData *source) {
  VmLub_MeshData *result = vm_lub_new(&vm_lub_types[23], sizeof(*result));
  result->f_positions =
      vm_lub_list(&vm_lub_types[18], NULL, source->positions,
                  source->positions_count, sizeof(*source->positions));
  result->f_normals =
      vm_lub_list(&vm_lub_types[18], NULL, source->normals,
                  source->normals_count, sizeof(*source->normals));
  result->f_indices =
      vm_lub_list(&vm_lub_types[19], NULL, source->indices,
                  source->indices_count, sizeof(*source->indices));
  result->f_vert_count = source->vert_count;
  result->f_index_count = source->index_count;
  result->f_uvs =
      (source->uvs ? vm_lub_list(&vm_lub_types[18], NULL, source->uvs,
                                 source->uvs_count, sizeof(*source->uvs))
                   : NULL);
  result->f_tangents =
      (source->tangents
           ? vm_lub_list(&vm_lub_types[18], NULL, source->tangents,
                         source->tangents_count, sizeof(*source->tangents))
           : NULL);
  result->f_bounds_min =
      (source->bounds_min
           ? vm_lub_list(&vm_lub_types[18], NULL, source->bounds_min,
                         source->bounds_min_count, sizeof(*source->bounds_min))
           : NULL);
  result->f_bounds_max =
      (source->bounds_max
           ? vm_lub_list(&vm_lub_types[18], NULL, source->bounds_max,
                         source->bounds_max_count, sizeof(*source->bounds_max))
           : NULL);
  result->f_cell = (VmLubOptF32){source->has_cell, source->cell};
  result->f_colors =
      (source->colors
           ? vm_lub_list(&vm_lub_types[18], NULL, source->colors,
                         source->colors_count, sizeof(*source->colors))
           : NULL);
  result->f_metal_rough =
      (source->metal_rough
           ? vm_lub_list(&vm_lub_types[18], NULL, source->metal_rough,
                         source->metal_rough_count,
                         sizeof(*source->metal_rough))
           : NULL);
  result->f_joints =
      (source->joints
           ? vm_lub_list(&vm_lub_types[19], NULL, source->joints,
                         source->joints_count, sizeof(*source->joints))
           : NULL);
  result->f_weights =
      (source->weights
           ? vm_lub_list(&vm_lub_types[18], NULL, source->weights,
                         source->weights_count, sizeof(*source->weights))
           : NULL);
  result->f_bones =
      (source->bones
           ? vm_lub_from_SdfBone_list(NULL, source->bones, source->bones_count)
           : NULL);
  return result;
}
static VmLub_FontMetrics *
vm_lub_from_FontMetrics(const LubFontMetrics *source) {
  VmLub_FontMetrics *result = vm_lub_new(&vm_lub_types[24], sizeof(*result));
  result->f_ascent = source->ascent;
  result->f_descent = source->descent;
  result->f_line_gap = source->line_gap;
  return result;
}
static VmLub_GlyphBitmap *
vm_lub_from_GlyphBitmap(const LubGlyphBitmap *source) {
  VmLub_GlyphBitmap *result = vm_lub_new(&vm_lub_types[25], sizeof(*result));
  result->f_w = source->w;
  result->f_h = source->h;
  result->f_xoff = source->xoff;
  result->f_yoff = source->yoff;
  result->f_advance = source->advance;
  result->f_bytes = vm_lub_view_Bytes(source->bytes);
  return result;
}
static VmLub_GlyphMesh *vm_lub_from_GlyphMesh(const LubGlyphMesh *source) {
  VmLub_GlyphMesh *result = vm_lub_new(&vm_lub_types[26], sizeof(*result));
  result->f_positions = vm_lub_list(
      &vm_lub_types[18], NULL, source->base.positions,
      source->base.positions_count, sizeof(*source->base.positions));
  result->f_normals =
      vm_lub_list(&vm_lub_types[18], NULL, source->base.normals,
                  source->base.normals_count, sizeof(*source->base.normals));
  result->f_indices =
      vm_lub_list(&vm_lub_types[19], NULL, source->base.indices,
                  source->base.indices_count, sizeof(*source->base.indices));
  result->f_vert_count = source->base.vert_count;
  result->f_index_count = source->base.index_count;
  result->f_uvs =
      (source->base.uvs
           ? vm_lub_list(&vm_lub_types[18], NULL, source->base.uvs,
                         source->base.uvs_count, sizeof(*source->base.uvs))
           : NULL);
  result->f_tangents =
      (source->base.tangents
           ? vm_lub_list(&vm_lub_types[18], NULL, source->base.tangents,
                         source->base.tangents_count,
                         sizeof(*source->base.tangents))
           : NULL);
  result->f_bounds_min =
      (source->base.bounds_min
           ? vm_lub_list(&vm_lub_types[18], NULL, source->base.bounds_min,
                         source->base.bounds_min_count,
                         sizeof(*source->base.bounds_min))
           : NULL);
  result->f_bounds_max =
      (source->base.bounds_max
           ? vm_lub_list(&vm_lub_types[18], NULL, source->base.bounds_max,
                         source->base.bounds_max_count,
                         sizeof(*source->base.bounds_max))
           : NULL);
  result->f_cell = (VmLubOptF32){source->base.has_cell, source->base.cell};
  result->f_colors =
      (source->base.colors
           ? vm_lub_list(&vm_lub_types[18], NULL, source->base.colors,
                         source->base.colors_count,
                         sizeof(*source->base.colors))
           : NULL);
  result->f_metal_rough =
      (source->base.metal_rough
           ? vm_lub_list(&vm_lub_types[18], NULL, source->base.metal_rough,
                         source->base.metal_rough_count,
                         sizeof(*source->base.metal_rough))
           : NULL);
  result->f_joints =
      (source->base.joints
           ? vm_lub_list(&vm_lub_types[19], NULL, source->base.joints,
                         source->base.joints_count,
                         sizeof(*source->base.joints))
           : NULL);
  result->f_weights =
      (source->base.weights
           ? vm_lub_list(&vm_lub_types[18], NULL, source->base.weights,
                         source->base.weights_count,
                         sizeof(*source->base.weights))
           : NULL);
  result->f_bones =
      (source->base.bones ? vm_lub_from_SdfBone_list(NULL, source->base.bones,
                                                     source->base.bones_count)
                          : NULL);
  result->f_advance = source->advance;
  return result;
}
static VmLub_AudioInfo *vm_lub_from_AudioInfo(const LubAudioInfo *source) {
  VmLub_AudioInfo *result = vm_lub_new(&vm_lub_types[27], sizeof(*result));
  result->f_device = source->device;
  result->f_rate = source->rate;
  result->f_voices = source->voices;
  result->f_snds = source->snds;
  return result;
}
static VmLub_ShapeView *vm_lub_from_ShapeView(const LubShapeView *source) {
  VmLub_ShapeView *result = vm_lub_new(&vm_lub_types[28], sizeof(*result));
  result->f_body = vm_lub_string(source->body);
  result->f_shape = vm_lub_string(source->shape);
  result->f_tag = vm_lub_string(source->tag);
  result->f_chain = vm_lub_string(source->chain);
  result->f_segment = (VmLubOptBool){source->has_segment, source->segment};
  result->f_material_name = vm_lub_string(source->material_name);
  result->f_material_id =
      (VmLubOptI32){source->has_material_id, source->material_id};
  result->f_kind = (VmLubOptI32){source->has_kind, source->kind};
  result->f_category_bits = !source->has_category_bits
                                ? NULL
                                : vm_lub_bits_string(source->category_bits);
  result->f_mask_bits =
      !source->has_mask_bits ? NULL : vm_lub_bits_string(source->mask_bits);
  result->f_group = (VmLubOptI32){source->has_group, source->group};
  result->f_valid = source->valid;
  return result;
}
static VmLub_PreSolveContact *
vm_lub_from_PreSolveContact(const LubPreSolveContact *source) {
  VmLub_PreSolveContact *result =
      vm_lub_new(&vm_lub_types[29], sizeof(*result));
  result->f_a = vm_lub_from_ShapeView(&source->a);
  result->f_b = vm_lub_from_ShapeView(&source->b);
  result->f_nx = source->nx;
  result->f_ny = source->ny;
  result->f_rolling_impulse = source->rolling_impulse;
  result->f_point_count = source->point_count;
  result->f_points = vm_lub_from_ManifoldPoint_list(NULL, source->points,
                                                    source->points_count);
  result->f_x = (VmLubOptF32){source->has_x, source->x};
  result->f_y = (VmLubOptF32){source->has_y, source->y};
  result->f_separation =
      (VmLubOptF32){source->has_separation, source->separation};
  result->f_normal_velocity =
      (VmLubOptF32){source->has_normal_velocity, source->normal_velocity};
  return result;
}
static VmLub_ManifoldPoint *
vm_lub_from_ManifoldPoint(const LubManifoldPoint *source) {
  VmLub_ManifoldPoint *result = vm_lub_new(&vm_lub_types[30], sizeof(*result));
  result->f_x = source->x;
  result->f_y = source->y;
  result->f_anchor_a_x = source->anchor_a_x;
  result->f_anchor_a_y = source->anchor_a_y;
  result->f_anchor_b_x = source->anchor_b_x;
  result->f_anchor_b_y = source->anchor_b_y;
  result->f_separation = source->separation;
  result->f_normal_impulse = source->normal_impulse;
  result->f_tangent_impulse = source->tangent_impulse;
  result->f_total_normal_impulse = source->total_normal_impulse;
  result->f_normal_velocity = source->normal_velocity;
  result->f_id = source->id;
  result->f_persisted = source->persisted;
  return result;
}
static VmLub_MaterialView *
vm_lub_from_MaterialView(const LubMaterialView *source) {
  VmLub_MaterialView *result = vm_lub_new(&vm_lub_types[31], sizeof(*result));
  result->f_friction = (VmLubOptF32){source->has_friction, source->friction};
  result->f_restitution =
      (VmLubOptF32){source->has_restitution, source->restitution};
  result->f_material_id = source->material_id;
  return result;
}
static VmLub_WorldInfo *vm_lub_from_WorldInfo(const LubWorldInfo *source) {
  VmLub_WorldInfo *result = vm_lub_new(&vm_lub_types[32], sizeof(*result));
  result->f_key = vm_lub_string(source->key);
  result->f_valid = source->valid;
  result->f_version = source->version;
  result->f_generation = source->generation;
  result->f_begun = source->begun;
  result->f_prune = source->prune;
  result->f_fixed_dt = source->fixed_dt;
  result->f_substeps = source->substeps;
  result->f_max_steps = source->max_steps;
  result->f_accumulator = source->accumulator;
  result->f_pending_commands = source->pending_commands;
  result->f_callbacks = vm_lub_from_WorldCallbackInfo(&source->callbacks);
  result->f_gravity =
      source->has_gravity ? vm_lub_from_Vec2d(&source->gravity) : NULL;
  result->f_sleep = (VmLubOptBool){source->has_sleep, source->sleep};
  result->f_continuous =
      (VmLubOptBool){source->has_continuous, source->continuous};
  result->f_warm_starting =
      (VmLubOptBool){source->has_warm_starting, source->warm_starting};
  result->f_restitution_threshold = (VmLubOptF32){
      source->has_restitution_threshold, source->restitution_threshold};
  result->f_hit_event_threshold = (VmLubOptF32){source->has_hit_event_threshold,
                                                source->hit_event_threshold};
  result->f_maximum_linear_speed = (VmLubOptF32){
      source->has_maximum_linear_speed, source->maximum_linear_speed};
  result->f_awake_body_count =
      (VmLubOptI32){source->has_awake_body_count, source->awake_body_count};
  return result;
}
static VmLub_WorldCallbackInfo *
vm_lub_from_WorldCallbackInfo(const LubWorldCallbackInfo *source) {
  VmLub_WorldCallbackInfo *result =
      vm_lub_new(&vm_lub_types[33], sizeof(*result));
  result->f_filter = source->filter;
  result->f_pre_solve = source->pre_solve;
  result->f_friction = source->friction;
  result->f_restitution = source->restitution;
  return result;
}
static VmLub_Vec2d *vm_lub_from_Vec2d(const LubVec2d *source) {
  VmLub_Vec2d *result = vm_lub_new(&vm_lub_types[34], sizeof(*result));
  result->f_x = source->x;
  result->f_y = source->y;
  return result;
}
static VmLub_JointInfo *vm_lub_from_JointInfo(const LubJointInfo *source) {
  VmLub_JointInfo *result = vm_lub_new(&vm_lub_types[35], sizeof(*result));
  result->f_joint = vm_lub_string(source->base.joint);
  result->f_type = source->base.type;
  result->f_a = vm_lub_string(source->base.a);
  result->f_b = vm_lub_string(source->base.b);
  result->f_valid = source->base.valid;
  result->f_collide_connected = source->collide_connected;
  result->f_force = vm_lub_from_Vec2d(&source->force);
  result->f_torque = source->torque;
  result->f_linear_separation = source->linear_separation;
  result->f_angular_separation = source->angular_separation;
  result->f_local_anchor_a = source->has_local_anchor_a
                                 ? vm_lub_from_Vec2d(&source->local_anchor_a)
                                 : NULL;
  result->f_local_anchor_b = source->has_local_anchor_b
                                 ? vm_lub_from_Vec2d(&source->local_anchor_b)
                                 : NULL;
  result->f_local_axis_a = source->has_local_axis_a
                               ? vm_lub_from_Vec2d(&source->local_axis_a)
                               : NULL;
  result->f_reference_angle =
      (VmLubOptF32){source->has_reference_angle, source->reference_angle};
  return result;
}
static VmLub_StepInfo *vm_lub_from_StepInfo(const LubStepInfo *source) {
  VmLub_StepInfo *result = vm_lub_new(&vm_lub_types[36], sizeof(*result));
  result->f_steps = source->steps;
  result->f_commands = source->commands;
  result->f_alpha = source->alpha;
  result->f_dropped = source->dropped;
  result->f_contact_begins = source->contact_begins;
  result->f_contact_ends = source->contact_ends;
  result->f_contact_hits = source->contact_hits;
  result->f_sensor_begins = source->sensor_begins;
  result->f_sensor_ends = source->sensor_ends;
  result->f_body_moves = source->body_moves;
  result->f_body_events = source->body_events;
  return result;
}
static VmLub_Pose *vm_lub_from_Pose(const LubPose *source) {
  VmLub_Pose *result = vm_lub_new(&vm_lub_types[37], sizeof(*result));
  result->f_x = source->x;
  result->f_y = source->y;
  result->f_angle = source->angle;
  result->f_vx = source->vx;
  result->f_vy = source->vy;
  result->f_w = source->w;
  result->f_awake = source->awake;
  result->f_enabled = source->enabled;
  result->f_sleep = source->sleep;
  result->f_sleep_threshold = source->sleep_threshold;
  return result;
}
static VmLub_Velocity *vm_lub_from_Velocity(const LubVelocity *source) {
  VmLub_Velocity *result = vm_lub_new(&vm_lub_types[38], sizeof(*result));
  result->f_x = source->x;
  result->f_y = source->y;
  result->f_w = source->w;
  return result;
}
static VmLub_MassData *vm_lub_from_MassData(const LubMassData *source) {
  VmLub_MassData *result = vm_lub_new(&vm_lub_types[39], sizeof(*result));
  result->f_mass = source->mass;
  result->f_inertia = source->inertia;
  result->f_center = vm_lub_from_Vec2d(&source->center);
  result->f_local_center = vm_lub_from_Vec2d(&source->local_center);
  return result;
}
static VmLub_JointView *vm_lub_from_JointView(const LubJointView *source) {
  VmLub_JointView *result = vm_lub_new(&vm_lub_types[40], sizeof(*result));
  result->f_joint = vm_lub_string(source->joint);
  result->f_type = source->type;
  result->f_a = vm_lub_string(source->a);
  result->f_b = vm_lub_string(source->b);
  result->f_valid = source->valid;
  return result;
}
static VmLub_ContactData *
vm_lub_from_ContactData(const LubContactData *source) {
  VmLub_ContactData *result = vm_lub_new(&vm_lub_types[41], sizeof(*result));
  result->f_a = vm_lub_from_ShapeView(&source->a);
  result->f_b = vm_lub_from_ShapeView(&source->b);
  result->f_nx = source->nx;
  result->f_ny = source->ny;
  result->f_point_count = source->point_count;
  result->f_x = (VmLubOptF32){source->has_x, source->x};
  result->f_y = (VmLubOptF32){source->has_y, source->y};
  result->f_separation =
      (VmLubOptF32){source->has_separation, source->separation};
  return result;
}
static VmLub_ShapeRayHit *
vm_lub_from_ShapeRayHit(const LubShapeRayHit *source) {
  VmLub_ShapeRayHit *result = vm_lub_new(&vm_lub_types[42], sizeof(*result));
  result->f_x = source->x;
  result->f_y = source->y;
  result->f_nx = source->nx;
  result->f_ny = source->ny;
  result->f_fraction = source->fraction;
  result->f_iterations = source->iterations;
  return result;
}
static VmLub_Aabb *vm_lub_from_Aabb(const LubAabb *source) {
  VmLub_Aabb *result = vm_lub_new(&vm_lub_types[43], sizeof(*result));
  result->f_min_x = source->min_x;
  result->f_min_y = source->min_y;
  result->f_max_x = source->max_x;
  result->f_max_y = source->max_y;
  return result;
}
static VmLub_ShapeInfo *vm_lub_from_ShapeInfo(const LubShapeInfo *source) {
  VmLub_ShapeInfo *result = vm_lub_new(&vm_lub_types[44], sizeof(*result));
  result->f_body = vm_lub_string(source->base.body);
  result->f_shape = vm_lub_string(source->base.shape);
  result->f_tag = vm_lub_string(source->base.tag);
  result->f_chain = vm_lub_string(source->base.chain);
  result->f_segment =
      (VmLubOptBool){source->base.has_segment, source->base.segment};
  result->f_material_name = vm_lub_string(source->base.material_name);
  result->f_material_id =
      (VmLubOptI32){source->base.has_material_id, source->base.material_id};
  result->f_kind = (VmLubOptI32){source->base.has_kind, source->base.kind};
  result->f_category_bits =
      !source->base.has_category_bits
          ? NULL
          : vm_lub_bits_string(source->base.category_bits);
  result->f_mask_bits = !source->base.has_mask_bits
                            ? NULL
                            : vm_lub_bits_string(source->base.mask_bits);
  result->f_group = (VmLubOptI32){source->base.has_group, source->base.group};
  result->f_valid = source->base.valid;
  result->f_density = source->density;
  result->f_friction = source->friction;
  result->f_restitution = source->restitution;
  result->f_sensor = source->sensor;
  result->f_sensor_events = source->sensor_events;
  result->f_contact = source->contact;
  result->f_pre_solve = source->pre_solve;
  result->f_hit = source->hit;
  result->f_filter = vm_lub_from_FilterInfo(&source->filter);
  result->f_aabb = vm_lub_from_Aabb(&source->aabb);
  return result;
}
static VmLub_FilterInfo *vm_lub_from_FilterInfo(const LubFilterInfo *source) {
  VmLub_FilterInfo *result = vm_lub_new(&vm_lub_types[45], sizeof(*result));
  result->f_category_bits = vm_lub_bits_string(source->category_bits);
  result->f_mask_bits = vm_lub_bits_string(source->mask_bits);
  result->f_group = source->group;
  return result;
}
static VmLub_ContactEvent *
vm_lub_from_ContactEvent(const LubContactEvent *source) {
  VmLub_ContactEvent *result = vm_lub_new(&vm_lub_types[46], sizeof(*result));
  result->f_a = vm_lub_from_ShapeView(&source->a);
  result->f_b = vm_lub_from_ShapeView(&source->b);
  result->f_nx = source->nx;
  result->f_ny = source->ny;
  result->f_point_count = source->point_count;
  result->f_x = source->x;
  result->f_y = source->y;
  result->f_approach_speed =
      (VmLubOptF32){source->has_approach_speed, source->approach_speed};
  return result;
}
static VmLub_BodyEvent *vm_lub_from_BodyEvent(const LubBodyEvent *source) {
  VmLub_BodyEvent *result = vm_lub_new(&vm_lub_types[47], sizeof(*result));
  result->f_body = vm_lub_string(source->body);
  result->f_valid = source->valid;
  result->f_x = source->x;
  result->f_y = source->y;
  result->f_angle = source->angle;
  result->f_fell_asleep = source->fell_asleep;
  return result;
}
static VmLub_SensorEvent *
vm_lub_from_SensorEvent(const LubSensorEvent *source) {
  VmLub_SensorEvent *result = vm_lub_new(&vm_lub_types[48], sizeof(*result));
  result->f_sensor = vm_lub_from_ShapeView(&source->sensor);
  result->f_visitor = vm_lub_from_ShapeView(&source->visitor);
  return result;
}
static VmLub_RayHit *vm_lub_from_RayHit(const LubRayHit *source) {
  VmLub_RayHit *result = vm_lub_new(&vm_lub_types[49], sizeof(*result));
  result->f_body = vm_lub_string(source->base.body);
  result->f_shape = vm_lub_string(source->base.shape);
  result->f_tag = vm_lub_string(source->base.tag);
  result->f_chain = vm_lub_string(source->base.chain);
  result->f_segment =
      (VmLubOptBool){source->base.has_segment, source->base.segment};
  result->f_material_name = vm_lub_string(source->base.material_name);
  result->f_material_id =
      (VmLubOptI32){source->base.has_material_id, source->base.material_id};
  result->f_kind = (VmLubOptI32){source->base.has_kind, source->base.kind};
  result->f_category_bits =
      !source->base.has_category_bits
          ? NULL
          : vm_lub_bits_string(source->base.category_bits);
  result->f_mask_bits = !source->base.has_mask_bits
                            ? NULL
                            : vm_lub_bits_string(source->base.mask_bits);
  result->f_group = (VmLubOptI32){source->base.has_group, source->base.group};
  result->f_valid = source->base.valid;
  result->f_x = source->x;
  result->f_y = source->y;
  result->f_nx = source->nx;
  result->f_ny = source->ny;
  result->f_fraction = source->fraction;
  result->f_node_visits =
      (VmLubOptI32){source->has_node_visits, source->node_visits};
  result->f_leaf_visits =
      (VmLubOptI32){source->has_leaf_visits, source->leaf_visits};
  return result;
}
static VmLub_MoverCast *vm_lub_from_MoverCast(const LubMoverCast *source) {
  VmLub_MoverCast *result = vm_lub_new(&vm_lub_types[50], sizeof(*result));
  result->f_fraction = source->fraction;
  result->f_dx = source->dx;
  result->f_dy = source->dy;
  return result;
}
static VmLub_MoverPlane *vm_lub_from_MoverPlane(const LubMoverPlane *source) {
  VmLub_MoverPlane *result = vm_lub_new(&vm_lub_types[51], sizeof(*result));
  result->f_body = vm_lub_string(source->base.body);
  result->f_shape = vm_lub_string(source->base.shape);
  result->f_tag = vm_lub_string(source->base.tag);
  result->f_chain = vm_lub_string(source->base.chain);
  result->f_segment =
      (VmLubOptBool){source->base.has_segment, source->base.segment};
  result->f_material_name = vm_lub_string(source->base.material_name);
  result->f_material_id =
      (VmLubOptI32){source->base.has_material_id, source->base.material_id};
  result->f_kind = (VmLubOptI32){source->base.has_kind, source->base.kind};
  result->f_category_bits =
      !source->base.has_category_bits
          ? NULL
          : vm_lub_bits_string(source->base.category_bits);
  result->f_mask_bits = !source->base.has_mask_bits
                            ? NULL
                            : vm_lub_bits_string(source->base.mask_bits);
  result->f_group = (VmLubOptI32){source->base.has_group, source->base.group};
  result->f_valid = source->base.valid;
  result->f_hit = source->hit;
  result->f_x = source->x;
  result->f_y = source->y;
  result->f_nx = source->nx;
  result->f_ny = source->ny;
  result->f_offset = source->offset;
  return result;
}
static VmLub_DebugData *vm_lub_from_DebugData(const LubDebugData *source) {
  VmLub_DebugData *result = vm_lub_new(&vm_lub_types[52], sizeof(*result));
  result->f_segments =
      vm_lub_list(&vm_lub_types[18], NULL, source->segments,
                  source->segments_count, sizeof(*source->segments));
  result->f_circles =
      vm_lub_list(&vm_lub_types[18], NULL, source->circles,
                  source->circles_count, sizeof(*source->circles));
  result->f_capsules =
      vm_lub_list(&vm_lub_types[18], NULL, source->capsules,
                  source->capsules_count, sizeof(*source->capsules));
  result->f_polygons =
      vm_lub_list(&vm_lub_types[18], NULL, source->polygons,
                  source->polygons_count, sizeof(*source->polygons));
  result->f_points = vm_lub_list(&vm_lub_types[18], NULL, source->points,
                                 source->points_count, sizeof(*source->points));
  return result;
}
static VmLub_Profile *vm_lub_from_Profile(const LubProfile *source) {
  VmLub_Profile *result = vm_lub_new(&vm_lub_types[53], sizeof(*result));
  result->f_step = source->step;
  result->f_pairs = source->pairs;
  result->f_collide = source->collide;
  result->f_solve = source->solve;
  result->f_merge_islands = source->merge_islands;
  result->f_prepare_stages = source->prepare_stages;
  result->f_solve_constraints = source->solve_constraints;
  result->f_prepare_constraints = source->prepare_constraints;
  result->f_integrate_velocities = source->integrate_velocities;
  result->f_warm_start = source->warm_start;
  result->f_solve_impulses = source->solve_impulses;
  result->f_integrate_positions = source->integrate_positions;
  result->f_relax_impulses = source->relax_impulses;
  result->f_apply_restitution = source->apply_restitution;
  result->f_store_impulses = source->store_impulses;
  result->f_split_islands = source->split_islands;
  result->f_transforms = source->transforms;
  result->f_hit_events = source->hit_events;
  result->f_refit = source->refit;
  result->f_bullets = source->bullets;
  result->f_sleep_islands = source->sleep_islands;
  result->f_sensors = source->sensors;
  return result;
}
static VmLub_Counters *vm_lub_from_Counters(const LubCounters *source) {
  VmLub_Counters *result = vm_lub_new(&vm_lub_types[54], sizeof(*result));
  result->f_body_count = source->body_count;
  result->f_shape_count = source->shape_count;
  result->f_contact_count = source->contact_count;
  result->f_joint_count = source->joint_count;
  result->f_island_count = source->island_count;
  result->f_stack_used = source->stack_used;
  result->f_static_tree_height = source->static_tree_height;
  result->f_tree_height = source->tree_height;
  result->f_byte_count = source->byte_count;
  result->f_task_count = source->task_count;
  result->f_color_counts =
      vm_lub_list(&vm_lub_types[19], NULL, source->color_counts,
                  source->color_counts_count, sizeof(*source->color_counts));
  return result;
}
static VmLub_ShapeView3d *
vm_lub_from_ShapeView3d(const LubShapeView3d *source) {
  VmLub_ShapeView3d *result = vm_lub_new(&vm_lub_types[55], sizeof(*result));
  result->f_body = vm_lub_string(source->body);
  result->f_shape = vm_lub_string(source->shape);
  result->f_tag = vm_lub_string(source->tag);
  result->f_material_name = vm_lub_string(source->material_name);
  result->f_material_id =
      (VmLubOptI32){source->has_material_id, source->material_id};
  result->f_kind = (VmLubOptI32){source->has_kind, source->kind};
  result->f_category_bits = !source->has_category_bits
                                ? NULL
                                : vm_lub_bits_string(source->category_bits);
  result->f_mask_bits =
      !source->has_mask_bits ? NULL : vm_lub_bits_string(source->mask_bits);
  result->f_group = (VmLubOptI32){source->has_group, source->group};
  result->f_valid = source->valid;
  return result;
}
static VmLub_PreSolveContact3d *
vm_lub_from_PreSolveContact3d(const LubPreSolveContact3d *source) {
  VmLub_PreSolveContact3d *result =
      vm_lub_new(&vm_lub_types[56], sizeof(*result));
  result->f_a = vm_lub_from_ShapeView3d(&source->a);
  result->f_b = vm_lub_from_ShapeView3d(&source->b);
  result->f_x = source->x;
  result->f_y = source->y;
  result->f_z = source->z;
  result->f_nx = source->nx;
  result->f_ny = source->ny;
  result->f_nz = source->nz;
  return result;
}
static VmLub_WorldInfo3d *
vm_lub_from_WorldInfo3d(const LubWorldInfo3d *source) {
  VmLub_WorldInfo3d *result = vm_lub_new(&vm_lub_types[57], sizeof(*result));
  result->f_key = vm_lub_string(source->key);
  result->f_valid = source->valid;
  result->f_version = source->version;
  result->f_generation = source->generation;
  result->f_begun = source->begun;
  result->f_prune = source->prune;
  result->f_fixed_dt = source->fixed_dt;
  result->f_substeps = source->substeps;
  result->f_max_steps = source->max_steps;
  result->f_accumulator = source->accumulator;
  result->f_pending_commands = source->pending_commands;
  result->f_gravity =
      source->has_gravity ? vm_lub_from_Vec3d(&source->gravity) : NULL;
  result->f_sleep = (VmLubOptBool){source->has_sleep, source->sleep};
  result->f_continuous =
      (VmLubOptBool){source->has_continuous, source->continuous};
  result->f_warm_starting =
      (VmLubOptBool){source->has_warm_starting, source->warm_starting};
  result->f_restitution_threshold = (VmLubOptF32){
      source->has_restitution_threshold, source->restitution_threshold};
  result->f_hit_event_threshold = (VmLubOptF32){source->has_hit_event_threshold,
                                                source->hit_event_threshold};
  result->f_maximum_linear_speed = (VmLubOptF32){
      source->has_maximum_linear_speed, source->maximum_linear_speed};
  result->f_awake_body_count =
      (VmLubOptI32){source->has_awake_body_count, source->awake_body_count};
  return result;
}
static VmLub_Vec3d *vm_lub_from_Vec3d(const LubVec3d *source) {
  VmLub_Vec3d *result = vm_lub_new(&vm_lub_types[58], sizeof(*result));
  result->f_x = source->x;
  result->f_y = source->y;
  result->f_z = source->z;
  return result;
}
static VmLub_JointInfo3d *
vm_lub_from_JointInfo3d(const LubJointInfo3d *source) {
  VmLub_JointInfo3d *result = vm_lub_new(&vm_lub_types[59], sizeof(*result));
  result->f_joint = vm_lub_string(source->base.joint);
  result->f_type = source->base.type;
  result->f_a = vm_lub_string(source->base.a);
  result->f_b = vm_lub_string(source->base.b);
  result->f_valid = source->base.valid;
  result->f_collide_connected = source->collide_connected;
  result->f_force = vm_lub_from_Vec3d(&source->force);
  result->f_torque = vm_lub_from_Vec3d(&source->torque);
  result->f_linear_separation = source->linear_separation;
  result->f_angular_separation = source->angular_separation;
  result->f_local_frame_a = vm_lub_from_Frame3d(&source->local_frame_a);
  result->f_local_frame_b = vm_lub_from_Frame3d(&source->local_frame_b);
  return result;
}
static VmLub_Frame3d *vm_lub_from_Frame3d(const LubFrame3d *source) {
  VmLub_Frame3d *result = vm_lub_new(&vm_lub_types[60], sizeof(*result));
  result->f_x = source->x;
  result->f_y = source->y;
  result->f_z = source->z;
  result->f_qx = source->qx;
  result->f_qy = source->qy;
  result->f_qz = source->qz;
  result->f_qw = source->qw;
  return result;
}
static VmLub_JointView3d *
vm_lub_from_JointView3d(const LubJointView3d *source) {
  VmLub_JointView3d *result = vm_lub_new(&vm_lub_types[61], sizeof(*result));
  result->f_joint = vm_lub_string(source->joint);
  result->f_type = source->type;
  result->f_a = vm_lub_string(source->a);
  result->f_b = vm_lub_string(source->b);
  result->f_valid = source->valid;
  return result;
}
static VmLub_MoverCast3d *
vm_lub_from_MoverCast3d(const LubMoverCast3d *source) {
  VmLub_MoverCast3d *result = vm_lub_new(&vm_lub_types[62], sizeof(*result));
  result->f_fraction = source->fraction;
  result->f_dx = source->dx;
  result->f_dy = source->dy;
  result->f_dz = source->dz;
  return result;
}
static VmLub_MoverPlane3d *
vm_lub_from_MoverPlane3d(const LubMoverPlane3d *source) {
  VmLub_MoverPlane3d *result = vm_lub_new(&vm_lub_types[63], sizeof(*result));
  result->f_body = vm_lub_string(source->base.body);
  result->f_shape = vm_lub_string(source->base.shape);
  result->f_tag = vm_lub_string(source->base.tag);
  result->f_material_name = vm_lub_string(source->base.material_name);
  result->f_material_id =
      (VmLubOptI32){source->base.has_material_id, source->base.material_id};
  result->f_kind = (VmLubOptI32){source->base.has_kind, source->base.kind};
  result->f_category_bits =
      !source->base.has_category_bits
          ? NULL
          : vm_lub_bits_string(source->base.category_bits);
  result->f_mask_bits = !source->base.has_mask_bits
                            ? NULL
                            : vm_lub_bits_string(source->base.mask_bits);
  result->f_group = (VmLubOptI32){source->base.has_group, source->base.group};
  result->f_valid = source->base.valid;
  result->f_x = source->x;
  result->f_y = source->y;
  result->f_z = source->z;
  result->f_nx = source->nx;
  result->f_ny = source->ny;
  result->f_nz = source->nz;
  result->f_offset = source->offset;
  result->f_plane_count = source->plane_count;
  return result;
}
static VmLub_StepInfo3d *vm_lub_from_StepInfo3d(const LubStepInfo3d *source) {
  VmLub_StepInfo3d *result = vm_lub_new(&vm_lub_types[64], sizeof(*result));
  result->f_steps = source->base.steps;
  result->f_commands = source->base.commands;
  result->f_alpha = source->base.alpha;
  result->f_dropped = source->base.dropped;
  result->f_contact_begins = source->base.contact_begins;
  result->f_contact_ends = source->base.contact_ends;
  result->f_contact_hits = source->base.contact_hits;
  result->f_sensor_begins = source->base.sensor_begins;
  result->f_sensor_ends = source->base.sensor_ends;
  result->f_body_moves = source->base.body_moves;
  result->f_body_events = source->base.body_events;
  result->f_joint_events = source->joint_events;
  return result;
}
static VmLub_Pose3d *vm_lub_from_Pose3d(const LubPose3d *source) {
  VmLub_Pose3d *result = vm_lub_new(&vm_lub_types[65], sizeof(*result));
  result->f_x = source->x;
  result->f_y = source->y;
  result->f_z = source->z;
  result->f_qx = source->qx;
  result->f_qy = source->qy;
  result->f_qz = source->qz;
  result->f_qw = source->qw;
  result->f_vx = source->vx;
  result->f_vy = source->vy;
  result->f_vz = source->vz;
  result->f_wx = source->wx;
  result->f_wy = source->wy;
  result->f_wz = source->wz;
  result->f_awake = source->awake;
  result->f_enabled = source->enabled;
  result->f_sleep = source->sleep;
  result->f_sleep_threshold = source->sleep_threshold;
  return result;
}
static VmLub_Velocity3d *vm_lub_from_Velocity3d(const LubVelocity3d *source) {
  VmLub_Velocity3d *result = vm_lub_new(&vm_lub_types[66], sizeof(*result));
  result->f_x = source->x;
  result->f_y = source->y;
  result->f_z = source->z;
  result->f_wx = source->wx;
  result->f_wy = source->wy;
  result->f_wz = source->wz;
  return result;
}
static VmLub_MassData3d *vm_lub_from_MassData3d(const LubMassData3d *source) {
  VmLub_MassData3d *result = vm_lub_new(&vm_lub_types[67], sizeof(*result));
  result->f_mass = source->mass;
  result->f_center = vm_lub_from_Vec3d(&source->center);
  result->f_local_center = vm_lub_from_Vec3d(&source->local_center);
  result->f_inertia = vm_lub_from_Inertia3d(&source->inertia);
  return result;
}
static VmLub_Inertia3d *vm_lub_from_Inertia3d(const LubInertia3d *source) {
  VmLub_Inertia3d *result = vm_lub_new(&vm_lub_types[68], sizeof(*result));
  result->f_xx = source->xx;
  result->f_yy = source->yy;
  result->f_zz = source->zz;
  result->f_xy = source->xy;
  result->f_xz = source->xz;
  result->f_yz = source->yz;
  return result;
}
static VmLub_ContactEvent3d *
vm_lub_from_ContactEvent3d(const LubContactEvent3d *source) {
  VmLub_ContactEvent3d *result = vm_lub_new(&vm_lub_types[69], sizeof(*result));
  result->f_a = vm_lub_from_ShapeView3d(&source->a);
  result->f_b = vm_lub_from_ShapeView3d(&source->b);
  result->f_nx = source->nx;
  result->f_ny = source->ny;
  result->f_nz = source->nz;
  result->f_point_count = source->point_count;
  result->f_x = source->x;
  result->f_y = source->y;
  result->f_z = source->z;
  result->f_approach_speed =
      (VmLubOptF32){source->has_approach_speed, source->approach_speed};
  return result;
}
static VmLub_BodyEvent3d *
vm_lub_from_BodyEvent3d(const LubBodyEvent3d *source) {
  VmLub_BodyEvent3d *result = vm_lub_new(&vm_lub_types[70], sizeof(*result));
  result->f_body = vm_lub_string(source->body);
  result->f_valid = source->valid;
  result->f_x = source->x;
  result->f_y = source->y;
  result->f_z = source->z;
  result->f_qx = source->qx;
  result->f_qy = source->qy;
  result->f_qz = source->qz;
  result->f_qw = source->qw;
  result->f_fell_asleep = source->fell_asleep;
  return result;
}
static VmLub_SensorEvent3d *
vm_lub_from_SensorEvent3d(const LubSensorEvent3d *source) {
  VmLub_SensorEvent3d *result = vm_lub_new(&vm_lub_types[71], sizeof(*result));
  result->f_sensor = vm_lub_from_ShapeView3d(&source->sensor);
  result->f_visitor = vm_lub_from_ShapeView3d(&source->visitor);
  return result;
}
static VmLub_JointEvent3d *
vm_lub_from_JointEvent3d(const LubJointEvent3d *source) {
  VmLub_JointEvent3d *result = vm_lub_new(&vm_lub_types[72], sizeof(*result));
  result->f_joint = vm_lub_string(source->base.joint);
  result->f_type = source->base.type;
  result->f_a = vm_lub_string(source->base.a);
  result->f_b = vm_lub_string(source->base.b);
  result->f_valid = source->base.valid;
  return result;
}
static VmLub_RayHit3d *vm_lub_from_RayHit3d(const LubRayHit3d *source) {
  VmLub_RayHit3d *result = vm_lub_new(&vm_lub_types[73], sizeof(*result));
  result->f_body = vm_lub_string(source->base.body);
  result->f_shape = vm_lub_string(source->base.shape);
  result->f_tag = vm_lub_string(source->base.tag);
  result->f_material_name = vm_lub_string(source->base.material_name);
  result->f_material_id =
      (VmLubOptI32){source->base.has_material_id, source->base.material_id};
  result->f_kind = (VmLubOptI32){source->base.has_kind, source->base.kind};
  result->f_category_bits =
      !source->base.has_category_bits
          ? NULL
          : vm_lub_bits_string(source->base.category_bits);
  result->f_mask_bits = !source->base.has_mask_bits
                            ? NULL
                            : vm_lub_bits_string(source->base.mask_bits);
  result->f_group = (VmLubOptI32){source->base.has_group, source->base.group};
  result->f_valid = source->base.valid;
  result->f_x = source->x;
  result->f_y = source->y;
  result->f_z = source->z;
  result->f_nx = source->nx;
  result->f_ny = source->ny;
  result->f_nz = source->nz;
  result->f_fraction = source->fraction;
  result->f_hit_material_id = source->hit_material_id;
  result->f_triangle_index = source->triangle_index;
  result->f_child_index = source->child_index;
  result->f_node_visits =
      (VmLubOptI32){source->has_node_visits, source->node_visits};
  result->f_leaf_visits =
      (VmLubOptI32){source->has_leaf_visits, source->leaf_visits};
  return result;
}
static VmLub_ContactData3d *
vm_lub_from_ContactData3d(const LubContactData3d *source) {
  VmLub_ContactData3d *result = vm_lub_new(&vm_lub_types[74], sizeof(*result));
  result->f_a = vm_lub_from_ShapeView3d(&source->a);
  result->f_b = vm_lub_from_ShapeView3d(&source->b);
  result->f_nx = source->nx;
  result->f_ny = source->ny;
  result->f_nz = source->nz;
  result->f_manifold_count = source->manifold_count;
  result->f_point_count = source->point_count;
  result->f_x = (VmLubOptF32){source->has_x, source->x};
  result->f_y = (VmLubOptF32){source->has_y, source->y};
  result->f_z = (VmLubOptF32){source->has_z, source->z};
  result->f_separation =
      (VmLubOptF32){source->has_separation, source->separation};
  return result;
}
static VmLub_ShapeRayHit3d *
vm_lub_from_ShapeRayHit3d(const LubShapeRayHit3d *source) {
  VmLub_ShapeRayHit3d *result = vm_lub_new(&vm_lub_types[75], sizeof(*result));
  result->f_x = source->x;
  result->f_y = source->y;
  result->f_z = source->z;
  result->f_nx = source->nx;
  result->f_ny = source->ny;
  result->f_nz = source->nz;
  result->f_fraction = source->fraction;
  result->f_iterations = source->iterations;
  result->f_triangle_index = source->triangle_index;
  result->f_child_index = source->child_index;
  return result;
}
static VmLub_Aabb3d *vm_lub_from_Aabb3d(const LubAabb3d *source) {
  VmLub_Aabb3d *result = vm_lub_new(&vm_lub_types[76], sizeof(*result));
  result->f_min_x = source->min_x;
  result->f_min_y = source->min_y;
  result->f_min_z = source->min_z;
  result->f_max_x = source->max_x;
  result->f_max_y = source->max_y;
  result->f_max_z = source->max_z;
  return result;
}
static VmLub_ShapeInfo3d *
vm_lub_from_ShapeInfo3d(const LubShapeInfo3d *source) {
  VmLub_ShapeInfo3d *result = vm_lub_new(&vm_lub_types[77], sizeof(*result));
  result->f_body = vm_lub_string(source->base.body);
  result->f_shape = vm_lub_string(source->base.shape);
  result->f_tag = vm_lub_string(source->base.tag);
  result->f_material_name = vm_lub_string(source->base.material_name);
  result->f_material_id =
      (VmLubOptI32){source->base.has_material_id, source->base.material_id};
  result->f_kind = (VmLubOptI32){source->base.has_kind, source->base.kind};
  result->f_category_bits =
      !source->base.has_category_bits
          ? NULL
          : vm_lub_bits_string(source->base.category_bits);
  result->f_mask_bits = !source->base.has_mask_bits
                            ? NULL
                            : vm_lub_bits_string(source->base.mask_bits);
  result->f_group = (VmLubOptI32){source->base.has_group, source->base.group};
  result->f_valid = source->base.valid;
  result->f_density = source->density;
  result->f_friction = source->friction;
  result->f_restitution = source->restitution;
  result->f_sensor = source->sensor;
  result->f_sensor_events = source->sensor_events;
  result->f_contact = source->contact;
  result->f_pre_solve = source->pre_solve;
  result->f_hit = source->hit;
  result->f_filter = vm_lub_from_FilterInfo(&source->filter);
  result->f_aabb = vm_lub_from_Aabb3d(&source->aabb);
  return result;
}
static VmLub_Profile3d *vm_lub_from_Profile3d(const LubProfile3d *source) {
  VmLub_Profile3d *result = vm_lub_new(&vm_lub_types[78], sizeof(*result));
  result->f_step = source->step;
  result->f_pairs = source->pairs;
  result->f_collide = source->collide;
  result->f_solve = source->solve;
  result->f_solver_setup = source->solver_setup;
  result->f_constraints = source->constraints;
  result->f_prepare_constraints = source->prepare_constraints;
  result->f_integrate_velocities = source->integrate_velocities;
  result->f_warm_start = source->warm_start;
  result->f_solve_impulses = source->solve_impulses;
  result->f_integrate_positions = source->integrate_positions;
  result->f_relax_impulses = source->relax_impulses;
  result->f_apply_restitution = source->apply_restitution;
  result->f_store_impulses = source->store_impulses;
  result->f_split_islands = source->split_islands;
  result->f_transforms = source->transforms;
  result->f_sensor_hits = source->sensor_hits;
  result->f_joint_events = source->joint_events;
  result->f_hit_events = source->hit_events;
  result->f_refit = source->refit;
  result->f_bullets = source->bullets;
  result->f_sleep_islands = source->sleep_islands;
  result->f_sensors = source->sensors;
  return result;
}
static VmLub_Counters3d *vm_lub_from_Counters3d(const LubCounters3d *source) {
  VmLub_Counters3d *result = vm_lub_new(&vm_lub_types[79], sizeof(*result));
  result->f_body_count = source->body_count;
  result->f_shape_count = source->shape_count;
  result->f_contact_count = source->contact_count;
  result->f_joint_count = source->joint_count;
  result->f_island_count = source->island_count;
  result->f_stack_used = source->stack_used;
  result->f_arena_capacity = source->arena_capacity;
  result->f_static_tree_height = source->static_tree_height;
  result->f_tree_height = source->tree_height;
  result->f_sat_call_count = source->sat_call_count;
  result->f_sat_cache_hit_count = source->sat_cache_hit_count;
  result->f_byte_count = source->byte_count;
  result->f_task_count = source->task_count;
  result->f_awake_contact_count = source->awake_contact_count;
  result->f_recycled_contact_count = source->recycled_contact_count;
  result->f_distance_iterations = source->distance_iterations;
  result->f_push_back_iterations = source->push_back_iterations;
  result->f_root_iterations = source->root_iterations;
  result->f_color_counts =
      vm_lub_list(&vm_lub_types[19], NULL, source->color_counts,
                  source->color_counts_count, sizeof(*source->color_counts));
  result->f_manifold_counts = vm_lub_list(
      &vm_lub_types[19], NULL, source->manifold_counts,
      source->manifold_counts_count, sizeof(*source->manifold_counts));
  return result;
}
static VmLub_EventData *vm_lub_from_EventData(const LubEventData *source) {
  VmLub_EventData *result = vm_lub_new(&vm_lub_types[80], sizeof(*result));
  result->f_kind = source->kind;
  result->f_key = source->key;
  result->f_button = source->button;
  result->f_x = source->x;
  result->f_y = source->y;
  result->f_dx = source->dx;
  result->f_dy = source->dy;
  return result;
}
static LubSdfBone *vm_lub_to_SdfBone_list(void *source, int32_t *count) {
  *count = tvm_list_count(source);
  LubSdfBone *items = vm_lub_alloc((size_t)*count * sizeof(*items));
  for (int i = 0; i < *count; i++)
    items[i] = vm_lub_to_SdfBone(((VmLub_SdfBone **)tvm_list_data(source))[i]);
  return items;
}
static LubSdfNodeDesc *vm_lub_to_SdfNodeDesc_list(void *source,
                                                  int32_t *count) {
  *count = tvm_list_count(source);
  LubSdfNodeDesc *items = vm_lub_alloc((size_t)*count * sizeof(*items));
  for (int i = 0; i < *count; i++)
    items[i] =
        vm_lub_to_SdfNodeDesc(((VmLub_SdfNodeDesc **)tvm_list_data(source))[i]);
  return items;
}
static LubChainMaterial *vm_lub_to_ChainMaterial_list(void *source,
                                                      int32_t *count) {
  *count = tvm_list_count(source);
  LubChainMaterial *items = vm_lub_alloc((size_t)*count * sizeof(*items));
  for (int i = 0; i < *count; i++)
    items[i] = vm_lub_to_ChainMaterial(
        ((VmLub_ChainMaterial **)tvm_list_data(source))[i]);
  return items;
}
static LubSurfaceMaterial3d *vm_lub_to_SurfaceMaterial3d_list(void *source,
                                                              int32_t *count) {
  *count = tvm_list_count(source);
  LubSurfaceMaterial3d *items = vm_lub_alloc((size_t)*count * sizeof(*items));
  for (int i = 0; i < *count; i++)
    items[i] = vm_lub_to_SurfaceMaterial3d(
        ((VmLub_SurfaceMaterial3d **)tvm_list_data(source))[i]);
  return items;
}
static LubCompoundChild3d *vm_lub_to_CompoundChild3d_list(void *source,
                                                          int32_t *count) {
  *count = tvm_list_count(source);
  LubCompoundChild3d *items = vm_lub_alloc((size_t)*count * sizeof(*items));
  for (int i = 0; i < *count; i++)
    items[i] = vm_lub_to_CompoundChild3d(
        ((VmLub_CompoundChild3d **)tvm_list_data(source))[i]);
  return items;
}
static void *vm_lub_from_SdfBone_list(void *list, const LubSdfBone *source,
                                      int32_t count) {
  if (!list)
    list = vm_lub_list_new(&vm_lub_types[81]);
  if (!list)
    return NULL;
  int32_t base = tvm_list_count(list);
  if (!tvm_list_resize(vm_lub_vm, list, base + count)) {
    vm_lub_fail("allocation-overflow");
    return list;
  }
  for (int i = 0; i < count; i++)
    ((void **)tvm_list_data(list))[base + i] = vm_lub_from_SdfBone(&source[i]);
  return list;
}
static void *vm_lub_from_GltfPrimitive_list(void *list,
                                            const LubGltfPrimitive *source,
                                            int32_t count) {
  if (!list)
    list = vm_lub_list_new(&vm_lub_types[82]);
  if (!list)
    return NULL;
  int32_t base = tvm_list_count(list);
  if (!tvm_list_resize(vm_lub_vm, list, base + count)) {
    vm_lub_fail("allocation-overflow");
    return list;
  }
  for (int i = 0; i < count; i++)
    ((void **)tvm_list_data(list))[base + i] =
        vm_lub_from_GltfPrimitive(&source[i]);
  return list;
}
static void *vm_lub_from_ManifoldPoint_list(void *list,
                                            const LubManifoldPoint *source,
                                            int32_t count) {
  if (!list)
    list = vm_lub_list_new(&vm_lub_types[83]);
  if (!list)
    return NULL;
  int32_t base = tvm_list_count(list);
  if (!tvm_list_resize(vm_lub_vm, list, base + count)) {
    vm_lub_fail("allocation-overflow");
    return list;
  }
  for (int i = 0; i < count; i++)
    ((void **)tvm_list_data(list))[base + i] =
        vm_lub_from_ManifoldPoint(&source[i]);
  return list;
}
static void *vm_lub_from_ShapeView_list(void *list, const LubShapeView *source,
                                        int32_t count) {
  if (!list)
    list = vm_lub_list_new(&vm_lub_types[84]);
  if (!list)
    return NULL;
  int32_t base = tvm_list_count(list);
  if (!tvm_list_resize(vm_lub_vm, list, base + count)) {
    vm_lub_fail("allocation-overflow");
    return list;
  }
  for (int i = 0; i < count; i++)
    ((void **)tvm_list_data(list))[base + i] =
        vm_lub_from_ShapeView(&source[i]);
  return list;
}
static void *vm_lub_from_JointView_list(void *list, const LubJointView *source,
                                        int32_t count) {
  if (!list)
    list = vm_lub_list_new(&vm_lub_types[85]);
  if (!list)
    return NULL;
  int32_t base = tvm_list_count(list);
  if (!tvm_list_resize(vm_lub_vm, list, base + count)) {
    vm_lub_fail("allocation-overflow");
    return list;
  }
  for (int i = 0; i < count; i++)
    ((void **)tvm_list_data(list))[base + i] =
        vm_lub_from_JointView(&source[i]);
  return list;
}
static void *vm_lub_from_ContactData_list(void *list,
                                          const LubContactData *source,
                                          int32_t count) {
  if (!list)
    list = vm_lub_list_new(&vm_lub_types[86]);
  if (!list)
    return NULL;
  int32_t base = tvm_list_count(list);
  if (!tvm_list_resize(vm_lub_vm, list, base + count)) {
    vm_lub_fail("allocation-overflow");
    return list;
  }
  for (int i = 0; i < count; i++)
    ((void **)tvm_list_data(list))[base + i] =
        vm_lub_from_ContactData(&source[i]);
  return list;
}
static void *vm_lub_from_ContactEvent_list(void *list,
                                           const LubContactEvent *source,
                                           int32_t count) {
  if (!list)
    list = vm_lub_list_new(&vm_lub_types[87]);
  if (!list)
    return NULL;
  int32_t base = tvm_list_count(list);
  if (!tvm_list_resize(vm_lub_vm, list, base + count)) {
    vm_lub_fail("allocation-overflow");
    return list;
  }
  for (int i = 0; i < count; i++)
    ((void **)tvm_list_data(list))[base + i] =
        vm_lub_from_ContactEvent(&source[i]);
  return list;
}
static void *vm_lub_from_BodyEvent_list(void *list, const LubBodyEvent *source,
                                        int32_t count) {
  if (!list)
    list = vm_lub_list_new(&vm_lub_types[88]);
  if (!list)
    return NULL;
  int32_t base = tvm_list_count(list);
  if (!tvm_list_resize(vm_lub_vm, list, base + count)) {
    vm_lub_fail("allocation-overflow");
    return list;
  }
  for (int i = 0; i < count; i++)
    ((void **)tvm_list_data(list))[base + i] =
        vm_lub_from_BodyEvent(&source[i]);
  return list;
}
static void *vm_lub_from_SensorEvent_list(void *list,
                                          const LubSensorEvent *source,
                                          int32_t count) {
  if (!list)
    list = vm_lub_list_new(&vm_lub_types[89]);
  if (!list)
    return NULL;
  int32_t base = tvm_list_count(list);
  if (!tvm_list_resize(vm_lub_vm, list, base + count)) {
    vm_lub_fail("allocation-overflow");
    return list;
  }
  for (int i = 0; i < count; i++)
    ((void **)tvm_list_data(list))[base + i] =
        vm_lub_from_SensorEvent(&source[i]);
  return list;
}
static void *vm_lub_from_RayHit_list(void *list, const LubRayHit *source,
                                     int32_t count) {
  if (!list)
    list = vm_lub_list_new(&vm_lub_types[90]);
  if (!list)
    return NULL;
  int32_t base = tvm_list_count(list);
  if (!tvm_list_resize(vm_lub_vm, list, base + count)) {
    vm_lub_fail("allocation-overflow");
    return list;
  }
  for (int i = 0; i < count; i++)
    ((void **)tvm_list_data(list))[base + i] = vm_lub_from_RayHit(&source[i]);
  return list;
}
static void *vm_lub_from_MoverPlane_list(void *list,
                                         const LubMoverPlane *source,
                                         int32_t count) {
  if (!list)
    list = vm_lub_list_new(&vm_lub_types[91]);
  if (!list)
    return NULL;
  int32_t base = tvm_list_count(list);
  if (!tvm_list_resize(vm_lub_vm, list, base + count)) {
    vm_lub_fail("allocation-overflow");
    return list;
  }
  for (int i = 0; i < count; i++)
    ((void **)tvm_list_data(list))[base + i] =
        vm_lub_from_MoverPlane(&source[i]);
  return list;
}
static void *vm_lub_from_JointView3d_list(void *list,
                                          const LubJointView3d *source,
                                          int32_t count) {
  if (!list)
    list = vm_lub_list_new(&vm_lub_types[92]);
  if (!list)
    return NULL;
  int32_t base = tvm_list_count(list);
  if (!tvm_list_resize(vm_lub_vm, list, base + count)) {
    vm_lub_fail("allocation-overflow");
    return list;
  }
  for (int i = 0; i < count; i++)
    ((void **)tvm_list_data(list))[base + i] =
        vm_lub_from_JointView3d(&source[i]);
  return list;
}
static void *vm_lub_from_MoverPlane3d_list(void *list,
                                           const LubMoverPlane3d *source,
                                           int32_t count) {
  if (!list)
    list = vm_lub_list_new(&vm_lub_types[93]);
  if (!list)
    return NULL;
  int32_t base = tvm_list_count(list);
  if (!tvm_list_resize(vm_lub_vm, list, base + count)) {
    vm_lub_fail("allocation-overflow");
    return list;
  }
  for (int i = 0; i < count; i++)
    ((void **)tvm_list_data(list))[base + i] =
        vm_lub_from_MoverPlane3d(&source[i]);
  return list;
}
static void *vm_lub_from_ContactEvent3d_list(void *list,
                                             const LubContactEvent3d *source,
                                             int32_t count) {
  if (!list)
    list = vm_lub_list_new(&vm_lub_types[94]);
  if (!list)
    return NULL;
  int32_t base = tvm_list_count(list);
  if (!tvm_list_resize(vm_lub_vm, list, base + count)) {
    vm_lub_fail("allocation-overflow");
    return list;
  }
  for (int i = 0; i < count; i++)
    ((void **)tvm_list_data(list))[base + i] =
        vm_lub_from_ContactEvent3d(&source[i]);
  return list;
}
static void *vm_lub_from_BodyEvent3d_list(void *list,
                                          const LubBodyEvent3d *source,
                                          int32_t count) {
  if (!list)
    list = vm_lub_list_new(&vm_lub_types[95]);
  if (!list)
    return NULL;
  int32_t base = tvm_list_count(list);
  if (!tvm_list_resize(vm_lub_vm, list, base + count)) {
    vm_lub_fail("allocation-overflow");
    return list;
  }
  for (int i = 0; i < count; i++)
    ((void **)tvm_list_data(list))[base + i] =
        vm_lub_from_BodyEvent3d(&source[i]);
  return list;
}
static void *vm_lub_from_SensorEvent3d_list(void *list,
                                            const LubSensorEvent3d *source,
                                            int32_t count) {
  if (!list)
    list = vm_lub_list_new(&vm_lub_types[96]);
  if (!list)
    return NULL;
  int32_t base = tvm_list_count(list);
  if (!tvm_list_resize(vm_lub_vm, list, base + count)) {
    vm_lub_fail("allocation-overflow");
    return list;
  }
  for (int i = 0; i < count; i++)
    ((void **)tvm_list_data(list))[base + i] =
        vm_lub_from_SensorEvent3d(&source[i]);
  return list;
}
static void *vm_lub_from_JointEvent3d_list(void *list,
                                           const LubJointEvent3d *source,
                                           int32_t count) {
  if (!list)
    list = vm_lub_list_new(&vm_lub_types[97]);
  if (!list)
    return NULL;
  int32_t base = tvm_list_count(list);
  if (!tvm_list_resize(vm_lub_vm, list, base + count)) {
    vm_lub_fail("allocation-overflow");
    return list;
  }
  for (int i = 0; i < count; i++)
    ((void **)tvm_list_data(list))[base + i] =
        vm_lub_from_JointEvent3d(&source[i]);
  return list;
}
static void *vm_lub_from_RayHit3d_list(void *list, const LubRayHit3d *source,
                                       int32_t count) {
  if (!list)
    list = vm_lub_list_new(&vm_lub_types[98]);
  if (!list)
    return NULL;
  int32_t base = tvm_list_count(list);
  if (!tvm_list_resize(vm_lub_vm, list, base + count)) {
    vm_lub_fail("allocation-overflow");
    return list;
  }
  for (int i = 0; i < count; i++)
    ((void **)tvm_list_data(list))[base + i] = vm_lub_from_RayHit3d(&source[i]);
  return list;
}
static void *vm_lub_from_ShapeView3d_list(void *list,
                                          const LubShapeView3d *source,
                                          int32_t count) {
  if (!list)
    list = vm_lub_list_new(&vm_lub_types[99]);
  if (!list)
    return NULL;
  int32_t base = tvm_list_count(list);
  if (!tvm_list_resize(vm_lub_vm, list, base + count)) {
    vm_lub_fail("allocation-overflow");
    return list;
  }
  for (int i = 0; i < count; i++)
    ((void **)tvm_list_data(list))[base + i] =
        vm_lub_from_ShapeView3d(&source[i]);
  return list;
}
static void *vm_lub_from_ContactData3d_list(void *list,
                                            const LubContactData3d *source,
                                            int32_t count) {
  if (!list)
    list = vm_lub_list_new(&vm_lub_types[100]);
  if (!list)
    return NULL;
  int32_t base = tvm_list_count(list);
  if (!tvm_list_resize(vm_lub_vm, list, base + count)) {
    vm_lub_fail("allocation-overflow");
    return list;
  }
  for (int i = 0; i < count; i++)
    ((void **)tvm_list_data(list))[base + i] =
        vm_lub_from_ContactData3d(&source[i]);
  return list;
}
static TvmStatus vm_host_lub_bytes_get(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                       void *user) {
  (void)user;
  VM_LUB_ENTER();
  VmLub_Bytes *self = a[0].p;
  int32_t p_index = a[1].i;
  if (!self) {
    vm_lub_fail("null reference");
    VM_LUB_LEAVE(vm);
  }
  LubView view = vm_lub_view_load(self->host_value);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  if (p_index < 0 || p_index >= view.len) {
    vm_lub_fail("bounds");
    VM_LUB_LEAVE(vm);
  }
  r[0].i = view.ptr[p_index];
  VM_LUB_LEAVE(vm);
}
static LubBindings *vm_lub_bindings_alloc(VmLub_Bindings *owner,
                                          int32_t scale) {
  void *block = tvm_blob_new(vm_lub_vm, lub_bindings_size(scale));
  if (!block) {
    vm_lub_fail("allocation-overflow");
    return NULL;
  }
  owner->f_blob = block;
  lub_bindings_init((LubBindings *)block, scale);
  return (LubBindings *)block;
}
static LubBindings *vm_lub_bindings_block(VmLub_Bindings *owner) {
  return owner->f_blob ? (LubBindings *)owner->f_blob
                       : vm_lub_bindings_alloc(owner, 1);
}
static LubBindings *vm_lub_bindings_grow(VmLub_Bindings *owner) {
  LubBindings *old = (LubBindings *)owner->f_blob;
  LubBindings *grown =
      vm_lub_bindings_alloc(owner, lub_bindings_scale(old) * 2);
  if (!grown)
    return old;
  lub_bindings_grow_into(grown, old);
  return grown;
}
static TvmStatus vm_host_lub_gfx_main_tex(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                          void *user) {
  (void)a;
  (void)user;
  VM_LUB_ENTER();
  r[0].p = vm_lub_handle_TextureRef(lub_gfx_main_tex(vm_lub_context));
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_xr_active(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                       void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  bool result = {0};
  result = lub_xr_active(vm_lub_context);
  r[0].i = result;
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_xr_focused(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                        void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  bool result = {0};
  result = lub_xr_focused(vm_lub_context);
  r[0].i = result;
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_xr_view(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                     void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  int32_t p_eye = a[0].i;
  float p_near = a[1].f;
  float p_far = a[2].f;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubXrView result = {0};
  LubStatus status = lub_xr_view(vm_lub_context, p_eye, p_near, p_far, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_from_XrView(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_xr_input(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                      void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  int32_t p_hand = a[0].i;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubXrInput result = {0};
  LubStatus status = lub_xr_input(vm_lub_context, p_hand, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_from_XrInput(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_app_config(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                        void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_ConfigOpts *p_opts = a[0].p;
  LubConfigOpts n_opts = vm_lub_to_ConfigOpts(p_opts);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_app_config(vm_lub_context, p_opts ? &n_opts : NULL);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_app_quit(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                      void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  int32_t p_code = a[0].i;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  lub_app_quit(vm_lub_context, p_code);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_gfx_begin_pass(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                            void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_PassOpts *p_opts = a[0].p;
  LubPassOpts n_opts = vm_lub_to_PassOpts(p_opts);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status =
      lub_gfx_begin_pass(vm_lub_context, p_opts ? &n_opts : NULL);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_gfx_end_pass(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                          void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_gfx_end_pass(vm_lub_context);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_gfx_use_shader(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                            void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_key = a[0].p;
  void *p_vs = a[1].p;
  void *p_fs = a[2].p;
  VmLubOptI32 p_version = (VmLubOptI32){a[3].i != 0, a[4].i};
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  LubStatus status = lub_gfx_use_shader(
      vm_lub_context, vm_lub_str(p_key), vm_lub_str(p_vs), vm_lub_str(p_fs),
      p_version.has ? &p_version.v : NULL, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_handle_ShaderRef(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_gfx_use_shader_compute(TvmVm *vm, TvmSlot *a,
                                                    TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_key = a[0].p;
  void *p_src = a[1].p;
  VmLubOptI32 p_version = (VmLubOptI32){a[2].i != 0, a[3].i};
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  LubStatus status = lub_gfx_use_shader_compute(
      vm_lub_context, vm_lub_str(p_key), vm_lub_str(p_src),
      p_version.has ? &p_version.v : NULL, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_handle_ShaderRef(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_gfx_use_buffer(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                            void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_key = a[0].p;
  int32_t p_type = a[1].i;
  void *p_data = a[2].p;
  VmLubOptI32 p_version = (VmLubOptI32){a[3].i != 0, a[4].i};
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  LubStatus status =
      lub_gfx_use_buffer(vm_lub_context, vm_lub_str(p_key), p_type,
                         p_data ? tvm_list_data(p_data) : NULL,
                         p_data ? tvm_list_count(p_data) : 0,
                         p_version.has ? &p_version.v : NULL, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_handle_BufferRef(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_gfx_use_buffer_array(TvmVm *vm, TvmSlot *a,
                                                  TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_key = a[0].p;
  int32_t p_type = a[1].i;
  void *p_data = a[2].p;
  int32_t p_count = a[3].i;
  VmLubOptI32 p_version = (VmLubOptI32){a[4].i != 0, a[5].i};
  if (p_data && (uint32_t)p_count > (uint32_t)tvm_array_len(p_data)) {
    vm_lub_fail("lub-array-length");
    VM_LUB_LEAVE(vm);
  }
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  LubStatus status = lub_gfx_use_buffer_array(
      vm_lub_context, vm_lub_str(p_key), p_type,
      p_data ? (const float *)tvm_array_data(p_data) : NULL, p_count,
      p_version.has ? &p_version.v : NULL, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_handle_BufferRef(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_gfx_use_buffer_ints(TvmVm *vm, TvmSlot *a,
                                                 TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_key = a[0].p;
  int32_t p_type = a[1].i;
  void *p_data = a[2].p;
  VmLubOptI32 p_version = (VmLubOptI32){a[3].i != 0, a[4].i};
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  LubStatus status =
      lub_gfx_use_buffer_ints(vm_lub_context, vm_lub_str(p_key), p_type,
                              p_data ? tvm_list_data(p_data) : NULL,
                              p_data ? tvm_list_count(p_data) : 0,
                              p_version.has ? &p_version.v : NULL, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_handle_BufferRef(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_gfx_use_buffer_empty(TvmVm *vm, TvmSlot *a,
                                                  TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_key = a[0].p;
  int32_t p_type = a[1].i;
  int32_t p_count = a[2].i;
  VmLubOptI32 p_version = (VmLubOptI32){a[3].i != 0, a[4].i};
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  LubStatus status = lub_gfx_use_buffer_empty(
      vm_lub_context, vm_lub_str(p_key), p_type, p_count,
      p_version.has ? &p_version.v : NULL, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_handle_BufferRef(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_gfx_use_texture(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                             void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_key = a[0].p;
  int32_t p_w = a[1].i;
  int32_t p_h = a[2].i;
  int32_t p_fmt = a[3].i;
  void *p_px = a[4].p;
  VmLubOptI32 p_version = (VmLubOptI32){a[5].i != 0, a[6].i};
  VmLub_TextureOpts *p_opts = a[7].p;
  LubTextureOpts n_opts = vm_lub_to_TextureOpts(p_opts);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  LubStatus status = lub_gfx_use_texture(
      vm_lub_context, vm_lub_str(p_key), p_w, p_h, p_fmt,
      p_px ? tvm_list_data(p_px) : NULL, p_px ? tvm_list_count(p_px) : 0,
      p_version.has ? &p_version.v : NULL, p_opts ? &n_opts : NULL, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_handle_TextureRef(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_gfx_use_texture_bytes(TvmVm *vm, TvmSlot *a,
                                                   TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_key = a[0].p;
  int32_t p_w = a[1].i;
  int32_t p_h = a[2].i;
  int32_t p_fmt = a[3].i;
  VmLub_Bytes *p_px = a[4].p;
  VmLubOptI32 p_version = (VmLubOptI32){a[5].i != 0, a[6].i};
  VmLub_TextureOpts *p_opts = a[7].p;
  LubView n_px = {0};
  if (p_px)
    n_px = vm_lub_view_load(p_px->host_value);
  LubTextureOpts n_opts = vm_lub_to_TextureOpts(p_opts);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  LubStatus status = lub_gfx_use_texture_bytes(
      vm_lub_context, vm_lub_str(p_key), p_w, p_h, p_fmt, n_px.ptr, n_px.len,
      p_version.has ? &p_version.v : NULL, p_opts ? &n_opts : NULL, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_handle_TextureRef(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_gfx_frame_buffer(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                              void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  int32_t p_type = a[0].i;
  void *p_data = a[1].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  LubStatus status = lub_gfx_frame_buffer(
      vm_lub_context, p_type, p_data ? tvm_list_data(p_data) : NULL,
      p_data ? tvm_list_count(p_data) : 0, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_handle_FrameRange(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_gfx_frame_buffer_array(TvmVm *vm, TvmSlot *a,
                                                    TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  int32_t p_type = a[0].i;
  void *p_data = a[1].p;
  int32_t p_count = a[2].i;
  if (p_data && (uint32_t)p_count > (uint32_t)tvm_array_len(p_data)) {
    vm_lub_fail("lub-array-length");
    VM_LUB_LEAVE(vm);
  }
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  LubStatus status = lub_gfx_frame_buffer_array(
      vm_lub_context, p_type,
      p_data ? (const float *)tvm_array_data(p_data) : NULL, p_count, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_handle_FrameRange(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_gfx_frame_buffer_ints(TvmVm *vm, TvmSlot *a,
                                                   TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  int32_t p_type = a[0].i;
  void *p_data = a[1].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  LubStatus status = lub_gfx_frame_buffer_ints(
      vm_lub_context, p_type, p_data ? tvm_list_data(p_data) : NULL,
      p_data ? tvm_list_count(p_data) : 0, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_handle_FrameRange(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_gfx_lookup_texture(TvmVm *vm, TvmSlot *a,
                                                TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_key = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  result = lub_gfx_lookup_texture(vm_lub_context, vm_lub_str(p_key));
  r[0].p = vm_lub_handle_TextureRef(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_gfx_lookup_shader(TvmVm *vm, TvmSlot *a,
                                               TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_key = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  result = lub_gfx_lookup_shader(vm_lub_context, vm_lub_str(p_key));
  r[0].p = vm_lub_handle_ShaderRef(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_gfx_lookup_buffer(TvmVm *vm, TvmSlot *a,
                                               TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_key = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  result = lub_gfx_lookup_buffer(vm_lub_context, vm_lub_str(p_key));
  r[0].p = vm_lub_handle_BufferRef(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_gfx_resource_info(TvmVm *vm, TvmSlot *a,
                                               TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  int32_t p_handle = a[0].i;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStr n_key = {0};
  int32_t n_version = {0};
  bool result = {0};
  result = lub_gfx_resource_info(vm_lub_context, p_handle, &n_key, &n_version);
  r[0].i = result;
  r[1].p = vm_lub_string(n_key);
  r[2].i = n_version;
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_gfx_readback(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                          void *user) {
  (void)user;
  VM_LUB_ENTER();
  if (!a[0].p) {
    vm_lub_fail("null reference");
    VM_LUB_LEAVE(vm);
  }
  VmLub_Readback *result = vm_lub_new(&vm_lub_types[101], sizeof(*result));
  result->host_value = vm_lub_key_store(vm_lub_str(a[0].p));
  r[0].p = result;
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_gfx_read_texture(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                              void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_Readback *p_rb = a[0].p;
  VmLub_TextureRef *p_tex = a[1].p;
  VmLubOptI32 p_id = (VmLubOptI32){a[2].i != 0, a[3].i};
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  int32_t n_status = {0};
  LubView n_bytes = {0};
  int32_t n_width = {0};
  int32_t n_height = {0};
  int32_t n_format = {0};
  int32_t n_stride = {0};
  int32_t n_result_id = {0};
  int32_t n_dropped = {0};
  LubStr n_error = {0};
  LubStatus status = lub_gfx_read_texture(
      vm_lub_context, vm_lub_key_load(p_rb ? p_rb->host_value : 0),
      (p_tex ? (int32_t)p_tex->host_value : 0), p_id.has ? &p_id.v : NULL,
      &n_status, &n_bytes, &n_width, &n_height, &n_format, &n_stride,
      &n_result_id, &n_dropped, &n_error);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].i = n_status;
  r[1].p = vm_lub_view_Bytes(n_bytes);
  r[2].i = n_width;
  r[3].i = n_height;
  r[4].i = n_format;
  r[5].i = n_stride;
  r[6].i = n_result_id;
  r[7].i = n_dropped;
  r[8].p = vm_lub_string(n_error);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_gfx_draw(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                      void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  int32_t p_count = a[0].i;
  VmLub_Bindings *p_bindings = a[1].p;
  VmLub_DrawOpts *p_opts = a[2].p;
  int32_t p_instances = a[3].i;
  if (!p_bindings) {
    vm_lub_fail("null reference");
    VM_LUB_LEAVE(vm);
  }
  LubBindings *n_bindings = vm_lub_bindings_block(p_bindings);
  LubDrawOpts n_opts = vm_lub_to_DrawOpts(p_opts);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_gfx_draw(vm_lub_context, p_count, n_bindings,
                                  p_opts ? &n_opts : NULL, p_instances);
  while (status == LUB_BINDINGS_FULL) {
    n_bindings = vm_lub_bindings_grow(p_bindings);
    status = lub_gfx_draw(vm_lub_context, p_count, n_bindings,
                          p_opts ? &n_opts : NULL, p_instances);
  }
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_gfx_bindings_set_texture(TvmVm *vm, TvmSlot *a,
                                                      TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_Bindings *p_b = a[0].p;
  void *p_name = a[1].p;
  VmLub_TextureRef *p_texture = a[2].p;
  if (!p_b) {
    vm_lub_fail("null reference");
    VM_LUB_LEAVE(vm);
  }
  LubBindings *n_b = vm_lub_bindings_block(p_b);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_gfx_bindings_set_texture(
      vm_lub_context, n_b, vm_lub_str(p_name),
      (p_texture ? (int32_t)p_texture->host_value : 0));
  while (status == LUB_BINDINGS_FULL) {
    n_b = vm_lub_bindings_grow(p_b);
    status = lub_gfx_bindings_set_texture(
        vm_lub_context, n_b, vm_lub_str(p_name),
        (p_texture ? (int32_t)p_texture->host_value : 0));
  }
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_gfx_bindings_set_buffer(TvmVm *vm, TvmSlot *a,
                                                     TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_Bindings *p_b = a[0].p;
  void *p_name = a[1].p;
  VmLub_BufferRef *p_buffer = a[2].p;
  if (!p_b) {
    vm_lub_fail("null reference");
    VM_LUB_LEAVE(vm);
  }
  LubBindings *n_b = vm_lub_bindings_block(p_b);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_gfx_bindings_set_buffer(
      vm_lub_context, n_b, vm_lub_str(p_name),
      (p_buffer ? (int32_t)p_buffer->host_value : 0));
  while (status == LUB_BINDINGS_FULL) {
    n_b = vm_lub_bindings_grow(p_b);
    status = lub_gfx_bindings_set_buffer(
        vm_lub_context, n_b, vm_lub_str(p_name),
        (p_buffer ? (int32_t)p_buffer->host_value : 0));
  }
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_gfx_bindings_set_frame_range(TvmVm *vm, TvmSlot *a,
                                                          TvmSlot *r,
                                                          void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_Bindings *p_b = a[0].p;
  void *p_name = a[1].p;
  VmLub_FrameRange *p_range = a[2].p;
  if (!p_b) {
    vm_lub_fail("null reference");
    VM_LUB_LEAVE(vm);
  }
  LubBindings *n_b = vm_lub_bindings_block(p_b);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_gfx_bindings_set_frame_range(
      vm_lub_context, n_b, vm_lub_str(p_name),
      (p_range ? (int32_t)p_range->host_value : 0));
  while (status == LUB_BINDINGS_FULL) {
    n_b = vm_lub_bindings_grow(p_b);
    status = lub_gfx_bindings_set_frame_range(
        vm_lub_context, n_b, vm_lub_str(p_name),
        (p_range ? (int32_t)p_range->host_value : 0));
  }
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_gfx_bindings_set_floats(TvmVm *vm, TvmSlot *a,
                                                     TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_Bindings *p_b = a[0].p;
  void *p_name = a[1].p;
  void *p_values = a[2].p;
  if (!p_b) {
    vm_lub_fail("null reference");
    VM_LUB_LEAVE(vm);
  }
  LubBindings *n_b = vm_lub_bindings_block(p_b);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status =
      lub_gfx_bindings_set_floats(vm_lub_context, n_b, vm_lub_str(p_name),
                                  p_values ? tvm_list_data(p_values) : NULL,
                                  p_values ? tvm_list_count(p_values) : 0);
  while (status == LUB_BINDINGS_FULL) {
    n_b = vm_lub_bindings_grow(p_b);
    status =
        lub_gfx_bindings_set_floats(vm_lub_context, n_b, vm_lub_str(p_name),
                                    p_values ? tvm_list_data(p_values) : NULL,
                                    p_values ? tvm_list_count(p_values) : 0);
  }
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_gfx_bindings_set4(TvmVm *vm, TvmSlot *a,
                                               TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_Bindings *p_b = a[0].p;
  void *p_name = a[1].p;
  int32_t p_count = a[2].i;
  float p_x = a[3].f;
  float p_y = a[4].f;
  float p_z = a[5].f;
  float p_w = a[6].f;
  if (!p_b) {
    vm_lub_fail("null reference");
    VM_LUB_LEAVE(vm);
  }
  LubBindings *n_b = vm_lub_bindings_block(p_b);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_gfx_bindings_set4(
      vm_lub_context, n_b, vm_lub_str(p_name), p_count, p_x, p_y, p_z, p_w);
  while (status == LUB_BINDINGS_FULL) {
    n_b = vm_lub_bindings_grow(p_b);
    status = lub_gfx_bindings_set4(vm_lub_context, n_b, vm_lub_str(p_name),
                                   p_count, p_x, p_y, p_z, p_w);
  }
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_gfx_bindings_set16(TvmVm *vm, TvmSlot *a,
                                                TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_Bindings *p_b = a[0].p;
  void *p_name = a[1].p;
  float p_m00 = a[2].f;
  float p_m01 = a[3].f;
  float p_m02 = a[4].f;
  float p_m03 = a[5].f;
  float p_m10 = a[6].f;
  float p_m11 = a[7].f;
  float p_m12 = a[8].f;
  float p_m13 = a[9].f;
  float p_m20 = a[10].f;
  float p_m21 = a[11].f;
  float p_m22 = a[12].f;
  float p_m23 = a[13].f;
  float p_m30 = a[14].f;
  float p_m31 = a[15].f;
  float p_m32 = a[16].f;
  float p_m33 = a[17].f;
  if (!p_b) {
    vm_lub_fail("null reference");
    VM_LUB_LEAVE(vm);
  }
  LubBindings *n_b = vm_lub_bindings_block(p_b);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_gfx_bindings_set16(
      vm_lub_context, n_b, vm_lub_str(p_name), p_m00, p_m01, p_m02, p_m03,
      p_m10, p_m11, p_m12, p_m13, p_m20, p_m21, p_m22, p_m23, p_m30, p_m31,
      p_m32, p_m33);
  while (status == LUB_BINDINGS_FULL) {
    n_b = vm_lub_bindings_grow(p_b);
    status = lub_gfx_bindings_set16(vm_lub_context, n_b, vm_lub_str(p_name),
                                    p_m00, p_m01, p_m02, p_m03, p_m10, p_m11,
                                    p_m12, p_m13, p_m20, p_m21, p_m22, p_m23,
                                    p_m30, p_m31, p_m32, p_m33);
  }
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_gfx_bindings_set_float_array(TvmVm *vm, TvmSlot *a,
                                                          TvmSlot *r,
                                                          void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_Bindings *p_b = a[0].p;
  void *p_name = a[1].p;
  void *p_values = a[2].p;
  if (!p_b) {
    vm_lub_fail("null reference");
    VM_LUB_LEAVE(vm);
  }
  LubBindings *n_b = vm_lub_bindings_block(p_b);
  if (!p_values) {
    vm_lub_fail("null reference");
    VM_LUB_LEAVE(vm);
  }
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_gfx_bindings_set_float_array(
      vm_lub_context, n_b, vm_lub_str(p_name),
      (const float *)tvm_array_data(p_values), tvm_array_len(p_values));
  while (status == LUB_BINDINGS_FULL) {
    n_b = vm_lub_bindings_grow(p_b);
    status = lub_gfx_bindings_set_float_array(
        vm_lub_context, n_b, vm_lub_str(p_name),
        (const float *)tvm_array_data(p_values), tvm_array_len(p_values));
  }
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_gfx_bindings_clear(TvmVm *vm, TvmSlot *a,
                                                TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_Bindings *p_b = a[0].p;
  if (!p_b) {
    vm_lub_fail("null reference");
    VM_LUB_LEAVE(vm);
  }
  LubBindings *n_b = vm_lub_bindings_block(p_b);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_gfx_bindings_clear(vm_lub_context, n_b);
  while (status == LUB_BINDINGS_FULL) {
    n_b = vm_lub_bindings_grow(p_b);
    status = lub_gfx_bindings_clear(vm_lub_context, n_b);
  }
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_gfx_dispatch(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                          void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  int32_t p_x = a[0].i;
  int32_t p_y = a[1].i;
  int32_t p_z = a[2].i;
  VmLub_Bindings *p_bindings = a[3].p;
  VmLub_DispatchOpts *p_opts = a[4].p;
  if (!p_bindings) {
    vm_lub_fail("null reference");
    VM_LUB_LEAVE(vm);
  }
  LubBindings *n_bindings = vm_lub_bindings_block(p_bindings);
  LubDispatchOpts n_opts = vm_lub_to_DispatchOpts(p_opts);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_gfx_dispatch(vm_lub_context, p_x, p_y, p_z, n_bindings,
                                      p_opts ? &n_opts : NULL);
  while (status == LUB_BINDINGS_FULL) {
    n_bindings = vm_lub_bindings_grow(p_bindings);
    status = lub_gfx_dispatch(vm_lub_context, p_x, p_y, p_z, n_bindings,
                              p_opts ? &n_opts : NULL);
  }
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_gfx_size(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                      void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  int32_t n_w = {0};
  int32_t n_h = {0};
  lub_gfx_size(vm_lub_context, &n_w, &n_h);
  r[0].i = n_w;
  r[1].i = n_h;
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_input_key_down(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                            void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_key = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  bool result = {0};
  result = lub_input_key_down(vm_lub_context, vm_lub_str(p_key));
  r[0].i = result;
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_input_key_pressed(TvmVm *vm, TvmSlot *a,
                                               TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_key = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  bool result = {0};
  result = lub_input_key_pressed(vm_lub_context, vm_lub_str(p_key));
  r[0].i = result;
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_input_key_released(TvmVm *vm, TvmSlot *a,
                                                TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_key = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  bool result = {0};
  result = lub_input_key_released(vm_lub_context, vm_lub_str(p_key));
  r[0].i = result;
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_input_mouse_down(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                              void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLubOptI32 p_button = (VmLubOptI32){a[0].i != 0, a[1].i};
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  bool result = {0};
  result =
      lub_input_mouse_down(vm_lub_context, p_button.has ? &p_button.v : NULL);
  r[0].i = result;
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_input_mouse_pressed(TvmVm *vm, TvmSlot *a,
                                                 TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLubOptI32 p_button = (VmLubOptI32){a[0].i != 0, a[1].i};
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  bool result = {0};
  result = lub_input_mouse_pressed(vm_lub_context,
                                   p_button.has ? &p_button.v : NULL);
  r[0].i = result;
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_input_mouse_released(TvmVm *vm, TvmSlot *a,
                                                  TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLubOptI32 p_button = (VmLubOptI32){a[0].i != 0, a[1].i};
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  bool result = {0};
  result = lub_input_mouse_released(vm_lub_context,
                                    p_button.has ? &p_button.v : NULL);
  r[0].i = result;
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_input_mouse_pos(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                             void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  float n_x = {0};
  float n_y = {0};
  lub_input_mouse_pos(vm_lub_context, &n_x, &n_y);
  r[0].f = n_x;
  r[1].f = n_y;
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_input_mouse_delta(TvmVm *vm, TvmSlot *a,
                                               TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  float n_dx = {0};
  float n_dy = {0};
  lub_input_mouse_delta(vm_lub_context, &n_dx, &n_dy);
  r[0].f = n_dx;
  r[1].f = n_dy;
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_input_focused(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                           void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  bool result = {0};
  result = lub_input_focused(vm_lub_context);
  r[0].i = result;
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_input_capture_mouse(TvmVm *vm, TvmSlot *a,
                                                 TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  bool p_captured = a[0].i != 0;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  bool result = {0};
  result = lub_input_capture_mouse(vm_lub_context, p_captured);
  r[0].i = result;
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_input_gamepad_connected(TvmVm *vm, TvmSlot *a,
                                                     TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  int32_t p_player = a[0].i;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  bool result = {0};
  result = lub_input_gamepad_connected(vm_lub_context, p_player);
  r[0].i = result;
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_input_gamepad_axis(TvmVm *vm, TvmSlot *a,
                                                TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  int32_t p_player = a[0].i;
  int32_t p_axis = a[1].i;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  float result = {0};
  result = lub_input_gamepad_axis(vm_lub_context, p_player, p_axis);
  r[0].f = result;
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_input_gamepad_down(TvmVm *vm, TvmSlot *a,
                                                TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  int32_t p_player = a[0].i;
  int32_t p_button = a[1].i;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  bool result = {0};
  result = lub_input_gamepad_down(vm_lub_context, p_player, p_button);
  r[0].i = result;
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_input_gamepad_pressed(TvmVm *vm, TvmSlot *a,
                                                   TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  int32_t p_player = a[0].i;
  int32_t p_button = a[1].i;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  bool result = {0};
  result = lub_input_gamepad_pressed(vm_lub_context, p_player, p_button);
  r[0].i = result;
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_input_gamepad_released(TvmVm *vm, TvmSlot *a,
                                                    TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  int32_t p_player = a[0].i;
  int32_t p_button = a[1].i;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  bool result = {0};
  result = lub_input_gamepad_released(vm_lub_context, p_player, p_button);
  r[0].i = result;
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_io_load_text(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                          void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_path = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStr n_text = {0};
  int32_t n_version = {0};
  int32_t n_status = {0};
  LubStr n_error = {0};
  LubStatus status = lub_io_load_text(vm_lub_context, vm_lub_str(p_path),
                                      &n_text, &n_version, &n_status, &n_error);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = vm_lub_string(n_text);
  r[1].i = n_version;
  r[2].i = n_status;
  r[3].p = vm_lub_string(n_error);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_io_save_text(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                          void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_path = a[0].p;
  void *p_text = a[1].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status =
      lub_io_save_text(vm_lub_context, vm_lub_str(p_path), vm_lub_str(p_text));
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_io_load_bytes(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                           void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_path = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubView n_bytes = {0};
  int32_t n_version = {0};
  int32_t n_status = {0};
  LubStr n_error = {0};
  LubStatus status =
      lub_io_load_bytes(vm_lub_context, vm_lub_str(p_path), &n_bytes,
                        &n_version, &n_status, &n_error);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = vm_lub_view_Bytes(n_bytes);
  r[1].i = n_version;
  r[2].i = n_status;
  r[3].p = vm_lub_string(n_error);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_io_load_floats(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                            void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_path = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  const float *n_data = {0};
  int32_t n_data_count = 0;
  int32_t n_version = {0};
  int32_t n_status = {0};
  LubStr n_error = {0};
  LubStatus status =
      lub_io_load_floats(vm_lub_context, vm_lub_str(p_path), &n_data,
                         &n_data_count, &n_version, &n_status, &n_error);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (n_data ? vm_lub_list(&vm_lub_types[18], NULL, n_data, n_data_count,
                                 sizeof(*n_data))
                   : NULL);
  r[1].i = n_version;
  r[2].i = n_status;
  r[3].p = vm_lub_string(n_error);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_io_load_gltf(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                          void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_path = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubGltfMesh n_mesh = {0};
  bool n_mesh_has = false;
  int32_t n_version = {0};
  int32_t n_status = {0};
  LubStr n_error = {0};
  LubStatus status =
      lub_io_load_gltf(vm_lub_context, vm_lub_str(p_path), &n_mesh, &n_mesh_has,
                       &n_version, &n_status, &n_error);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = n_mesh_has ? vm_lub_from_GltfMesh(&n_mesh) : NULL;
  r[1].i = n_version;
  r[2].i = n_status;
  r[3].p = vm_lub_string(n_error);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_io_interleave_pn(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                              void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_MeshData *p_mesh = a[0].p;
  LubMeshData n_mesh = vm_lub_to_MeshData(p_mesh);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  const float *result = {0};
  int32_t result_count = 0;
  LubStatus status = lub_io_interleave_pn(
      vm_lub_context, p_mesh ? &n_mesh : NULL, &result, &result_count);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND)
               ? NULL
               : vm_lub_list(&vm_lub_types[18], NULL, result, result_count,
                             sizeof(*result));
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_io_interleave_pncm(TvmVm *vm, TvmSlot *a,
                                                TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_MeshData *p_mesh = a[0].p;
  LubMeshData n_mesh = vm_lub_to_MeshData(p_mesh);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  const float *result = {0};
  int32_t result_count = 0;
  LubStatus status = lub_io_interleave_pncm(
      vm_lub_context, p_mesh ? &n_mesh : NULL, &result, &result_count);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND)
               ? NULL
               : vm_lub_list(&vm_lub_types[18], NULL, result, result_count,
                             sizeof(*result));
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_io_interleave_pncmw(TvmVm *vm, TvmSlot *a,
                                                 TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_MeshData *p_mesh = a[0].p;
  LubMeshData n_mesh = vm_lub_to_MeshData(p_mesh);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  const float *result = {0};
  int32_t result_count = 0;
  LubStatus status = lub_io_interleave_pncmw(
      vm_lub_context, p_mesh ? &n_mesh : NULL, &result, &result_count);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND)
               ? NULL
               : vm_lub_list(&vm_lub_types[18], NULL, result, result_count,
                             sizeof(*result));
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_io_interleave_pnu(TvmVm *vm, TvmSlot *a,
                                               TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_MeshData *p_mesh = a[0].p;
  LubMeshData n_mesh = vm_lub_to_MeshData(p_mesh);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  const float *result = {0};
  int32_t result_count = 0;
  LubStatus status = lub_io_interleave_pnu(
      vm_lub_context, p_mesh ? &n_mesh : NULL, &result, &result_count);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND)
               ? NULL
               : vm_lub_list(&vm_lub_types[18], NULL, result, result_count,
                             sizeof(*result));
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_io_interleave_pnut(TvmVm *vm, TvmSlot *a,
                                                TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_MeshData *p_mesh = a[0].p;
  LubMeshData n_mesh = vm_lub_to_MeshData(p_mesh);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  const float *result = {0};
  int32_t result_count = 0;
  LubStatus status = lub_io_interleave_pnut(
      vm_lub_context, p_mesh ? &n_mesh : NULL, &result, &result_count);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND)
               ? NULL
               : vm_lub_list(&vm_lub_types[18], NULL, result, result_count,
                             sizeof(*result));
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_mesh_surface_nets(TvmVm *vm, TvmSlot *a,
                                               TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_grid = a[0].p;
  int32_t p_nx = a[1].i;
  int32_t p_ny = a[2].i;
  int32_t p_nz = a[3].i;
  VmLubOptF32 p_cell = (VmLubOptF32){a[4].i != 0, a[5].f};
  VmLubOptF32 p_ox = (VmLubOptF32){a[6].i != 0, a[7].f};
  VmLubOptF32 p_oy = (VmLubOptF32){a[8].i != 0, a[9].f};
  VmLubOptF32 p_oz = (VmLubOptF32){a[10].i != 0, a[11].f};
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubMeshData result = {0};
  LubStatus status = lub_mesh_surface_nets(
      vm_lub_context, p_grid ? tvm_list_data(p_grid) : NULL,
      p_grid ? tvm_list_count(p_grid) : 0, p_nx, p_ny, p_nz,
      p_cell.has ? &p_cell.v : NULL, p_ox.has ? &p_ox.v : NULL,
      p_oy.has ? &p_oy.v : NULL, p_oz.has ? &p_oz.v : NULL, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_from_MeshData(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_mesh_sdf_mesh(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                           void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_nodes = a[0].p;
  int32_t p_root = a[1].i;
  int32_t p_n = a[2].i;
  VmLubOptF32 p_skin_k = (VmLubOptF32){a[3].i != 0, a[4].f};
  int32_t n_nodes_count = 0;
  LubSdfNodeDesc *n_nodes =
      p_nodes ? vm_lub_to_SdfNodeDesc_list(p_nodes, &n_nodes_count) : NULL;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubMeshData result = {0};
  LubStatus status =
      lub_mesh_sdf_mesh(vm_lub_context, n_nodes, n_nodes_count, p_root, p_n,
                        p_skin_k.has ? &p_skin_k.v : NULL, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_from_MeshData(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_font_metrics(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                          void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_Bytes *p_ttf = a[0].p;
  LubView n_ttf = {0};
  if (p_ttf)
    n_ttf = vm_lub_view_load(p_ttf->host_value);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubFontMetrics result = {0};
  LubStatus status =
      lub_font_metrics(vm_lub_context, n_ttf.ptr, n_ttf.len, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_from_FontMetrics(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_font_glyph(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                        void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_Bytes *p_ttf = a[0].p;
  int32_t p_codepoint = a[1].i;
  float p_px = a[2].f;
  LubView n_ttf = {0};
  if (p_ttf)
    n_ttf = vm_lub_view_load(p_ttf->host_value);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubGlyphBitmap result = {0};
  bool has = false;
  LubStatus status = lub_font_glyph(vm_lub_context, n_ttf.ptr, n_ttf.len,
                                    p_codepoint, p_px, &result, &has);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND || !has) ? NULL
                                             : vm_lub_from_GlyphBitmap(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_font_glyph_mesh(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                             void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_Bytes *p_ttf = a[0].p;
  int32_t p_codepoint = a[1].i;
  VmLubOptF32 p_tolerance = (VmLubOptF32){a[2].i != 0, a[3].f};
  LubView n_ttf = {0};
  if (p_ttf)
    n_ttf = vm_lub_view_load(p_ttf->host_value);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubGlyphMesh result = {0};
  bool has = false;
  LubStatus status = lub_font_glyph_mesh(
      vm_lub_context, n_ttf.ptr, n_ttf.len, p_codepoint,
      p_tolerance.has ? &p_tolerance.v : NULL, &result, &has);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p =
      (status == LUB_NOT_FOUND || !has) ? NULL : vm_lub_from_GlyphMesh(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_font_kern(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                       void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_Bytes *p_ttf = a[0].p;
  int32_t p_cp1 = a[1].i;
  int32_t p_cp2 = a[2].i;
  LubView n_ttf = {0};
  if (p_ttf)
    n_ttf = vm_lub_view_load(p_ttf->host_value);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  float result = {0};
  LubStatus status = lub_font_kern(vm_lub_context, n_ttf.ptr, n_ttf.len, p_cp1,
                                   p_cp2, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].f = result;
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_ui_render(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                       void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_ui_render(vm_lub_context);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_ui_begin_window(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                             void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_title = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  bool result = {0};
  result = lub_ui_begin_window(vm_lub_context, vm_lub_str(p_title));
  r[0].i = result;
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_ui_end_window(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                           void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  lub_ui_end_window(vm_lub_context);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_ui_text(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                     void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_s = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  lub_ui_text(vm_lub_context, vm_lub_str(p_s));
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_ui_button(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                       void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_label = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  bool result = {0};
  result = lub_ui_button(vm_lub_context, vm_lub_str(p_label));
  r[0].i = result;
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_ui_checkbox(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                         void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_label = a[0].p;
  bool p_v = a[1].i != 0;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  bool result = {0};
  result = lub_ui_checkbox(vm_lub_context, vm_lub_str(p_label), p_v);
  r[0].i = result;
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_ui_slider_float(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                             void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_label = a[0].p;
  float p_v = a[1].f;
  float p_min = a[2].f;
  float p_max = a[3].f;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  float result = {0};
  result = lub_ui_slider_float(vm_lub_context, vm_lub_str(p_label), p_v, p_min,
                               p_max);
  r[0].f = result;
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_ui_slider_int(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                           void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_label = a[0].p;
  int32_t p_v = a[1].i;
  int32_t p_min = a[2].i;
  int32_t p_max = a[3].i;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  int32_t result = {0};
  result =
      lub_ui_slider_int(vm_lub_context, vm_lub_str(p_label), p_v, p_min, p_max);
  r[0].i = result;
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_ui_drag_float(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                           void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_label = a[0].p;
  float p_v = a[1].f;
  VmLubOptF32 p_speed = (VmLubOptF32){a[2].i != 0, a[3].f};
  VmLubOptF32 p_min = (VmLubOptF32){a[4].i != 0, a[5].f};
  VmLubOptF32 p_max = (VmLubOptF32){a[6].i != 0, a[7].f};
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  float result = {0};
  result = lub_ui_drag_float(
      vm_lub_context, vm_lub_str(p_label), p_v, p_speed.has ? &p_speed.v : NULL,
      p_min.has ? &p_min.v : NULL, p_max.has ? &p_max.v : NULL);
  r[0].f = result;
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_ui_color_edit3(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                            void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_label = a[0].p;
  float p_r = a[1].f;
  float p_g = a[2].f;
  float p_b = a[3].f;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  float n_new_r = {0};
  float n_new_g = {0};
  float n_new_b = {0};
  lub_ui_color_edit3(vm_lub_context, vm_lub_str(p_label), p_r, p_g, p_b,
                     &n_new_r, &n_new_g, &n_new_b);
  r[0].f = n_new_r;
  r[1].f = n_new_g;
  r[2].f = n_new_b;
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_ui_separator(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                          void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  lub_ui_separator(vm_lub_context);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_ui_same_line(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                          void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  lub_ui_same_line(vm_lub_context);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_ui_tree_node(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                          void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_label = a[0].p;
  VmLubOptBool p_default_open = (VmLubOptBool){a[1].i != 0, a[2].i != 0};
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  bool result = {0};
  result = lub_ui_tree_node(vm_lub_context, vm_lub_str(p_label),
                            p_default_open.has ? &p_default_open.v : NULL);
  r[0].i = result;
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_ui_tree_pop(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                         void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  lub_ui_tree_pop(vm_lub_context);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_ui_set_next_window(TvmVm *vm, TvmSlot *a,
                                                TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  float p_x = a[0].f;
  float p_y = a[1].f;
  float p_w = a[2].f;
  float p_h = a[3].f;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  lub_ui_set_next_window(vm_lub_context, p_x, p_y, p_w, p_h);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_ui_want_capture_mouse(TvmVm *vm, TvmSlot *a,
                                                   TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  bool result = {0};
  result = lub_ui_want_capture_mouse(vm_lub_context);
  r[0].i = result;
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_host_available(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                            void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  bool result = {0};
  result = lub_host_available(vm_lub_context);
  r[0].i = result;
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_host_send(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                       void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_topic = a[0].p;
  void *p_payload = a[1].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  lub_host_send(vm_lub_context, vm_lub_str(p_topic), vm_lub_str(p_payload));
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_host_poll(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                       void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStr n_topic = {0};
  LubStr n_payload = {0};
  LubStatus status = lub_host_poll(vm_lub_context, &n_topic, &n_payload);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = vm_lub_string(n_topic);
  r[1].p = vm_lub_string(n_payload);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_audio_snd(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                       void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_key = a[0].p;
  void *p_data = a[1].p;
  int32_t p_channels = a[2].i;
  int32_t p_rate = a[3].i;
  VmLubOptI32 p_version = (VmLubOptI32){a[4].i != 0, a[5].i};
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  int32_t result = {0};
  bool has = false;
  LubStatus status = lub_audio_snd(
      vm_lub_context, vm_lub_str(p_key), p_data ? tvm_list_data(p_data) : NULL,
      p_data ? tvm_list_count(p_data) : 0, p_channels, p_rate,
      p_version.has ? &p_version.v : NULL, &result, &has);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  {
    VmLubOptI32 v = (VmLubOptI32){has, result};
    r[0].i = v.has;
    r[1].i = v.v;
  }
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_audio_snd_bytes(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                             void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_key = a[0].p;
  VmLub_Bytes *p_data = a[1].p;
  int32_t p_channels = a[2].i;
  int32_t p_rate = a[3].i;
  VmLubOptI32 p_version = (VmLubOptI32){a[4].i != 0, a[5].i};
  LubView n_data = {0};
  if (p_data)
    n_data = vm_lub_view_load(p_data->host_value);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  int32_t result = {0};
  bool has = false;
  LubStatus status = lub_audio_snd_bytes(
      vm_lub_context, vm_lub_str(p_key), n_data.ptr, n_data.len, p_channels,
      p_rate, p_version.has ? &p_version.v : NULL, &result, &has);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  {
    VmLubOptI32 v = (VmLubOptI32){has, result};
    r[0].i = v.has;
    r[1].i = v.v;
  }
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_audio_decode(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                          void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_Bytes *p_data = a[0].p;
  LubView n_data = {0};
  if (p_data)
    n_data = vm_lub_view_load(p_data->host_value);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubView n_bytes = {0};
  int32_t n_channels = {0};
  int32_t n_rate = {0};
  LubStatus status = lub_audio_decode(vm_lub_context, n_data.ptr, n_data.len,
                                      &n_bytes, &n_channels, &n_rate);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = vm_lub_view_Bytes(n_bytes);
  r[1].i = n_channels;
  r[2].i = n_rate;
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_audio_play(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                        void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  int32_t p_snd = a[0].i;
  VmLub_PlayOpts *p_opts = a[1].p;
  LubPlayOpts n_opts = vm_lub_to_PlayOpts(p_opts);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  bool result = {0};
  result = lub_audio_play(vm_lub_context, p_snd, p_opts ? &n_opts : NULL);
  r[0].i = result;
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_audio_voice(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                         void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_key = a[0].p;
  int32_t p_snd = a[1].i;
  VmLub_VoiceOpts *p_opts = a[2].p;
  LubVoiceOpts n_opts = vm_lub_to_VoiceOpts(p_opts);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  bool result = {0};
  result = lub_audio_voice(vm_lub_context, vm_lub_str(p_key), p_snd,
                           p_opts ? &n_opts : NULL);
  r[0].i = result;
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_audio_master_volume(TvmVm *vm, TvmSlot *a,
                                                 TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  float p_volume = a[0].f;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  lub_audio_master_volume(vm_lub_context, p_volume);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_audio_info(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                        void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubAudioInfo result = {0};
  lub_audio_info(vm_lub_context, &result);
  r[0].p = vm_lub_from_AudioInfo(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_sys_is_web(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                        void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  bool result = {0};
  result = lub_sys_is_web(vm_lub_context);
  r[0].i = result;
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_sys_fnv1a64(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                         void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_s = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  int32_t result = {0};
  result = lub_sys_fnv1a64(vm_lub_context, vm_lub_str(p_s));
  r[0].i = result;
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_sys_actual_fps(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                            void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  float result = {0};
  result = lub_sys_actual_fps(vm_lub_context);
  r[0].f = result;
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_profiler_enabled(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                              void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  bool result = {0};
  result = lub_profiler_enabled(vm_lub_context);
  r[0].i = result;
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_profiler_begin_scope(TvmVm *vm, TvmSlot *a,
                                                  TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_name = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  lub_profiler_begin_scope(vm_lub_context, vm_lub_str(p_name));
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_profiler_end_scope(TvmVm *vm, TvmSlot *a,
                                                TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_name = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  lub_profiler_end_scope(vm_lub_context, vm_lub_str(p_name));
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_profiler_reset(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                            void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  lub_profiler_reset(vm_lub_context);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_profiler_report(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                             void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_label = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  lub_profiler_report(vm_lub_context, vm_lub_str(p_label));
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_find_world(TvmVm *vm, TvmSlot *a,
                                               TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_key = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  result = lub_phys2d_find_world(vm_lub_context, vm_lub_str(p_key));
  r[0].p = vm_lub_handle_WorldRef(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_find_body(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                              void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef *p_world = a[0].p;
  void *p_key = a[1].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  result = lub_phys2d_find_body(vm_lub_context,
                                (p_world ? (int32_t)p_world->host_value : 0),
                                vm_lub_str(p_key));
  r[0].p = vm_lub_handle_BodyRef(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_find_shape(TvmVm *vm, TvmSlot *a,
                                               TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef *p_body = a[0].p;
  void *p_key = a[1].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  result = lub_phys2d_find_shape(vm_lub_context,
                                 (p_body ? (int32_t)p_body->host_value : 0),
                                 vm_lub_str(p_key));
  r[0].p = vm_lub_handle_ShapeRef(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_find_chain(TvmVm *vm, TvmSlot *a,
                                               TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef *p_body = a[0].p;
  void *p_key = a[1].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  result = lub_phys2d_find_chain(vm_lub_context,
                                 (p_body ? (int32_t)p_body->host_value : 0),
                                 vm_lub_str(p_key));
  r[0].p = vm_lub_handle_ChainRef(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_find_joint(TvmVm *vm, TvmSlot *a,
                                               TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef *p_world = a[0].p;
  void *p_key = a[1].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  result = lub_phys2d_find_joint(vm_lub_context,
                                 (p_world ? (int32_t)p_world->host_value : 0),
                                 vm_lub_str(p_key));
  r[0].p = vm_lub_handle_JointRef(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_world(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                          void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_key = a[0].p;
  VmLub_WorldOpts *p_opts = a[1].p;
  LubWorldOpts n_opts = vm_lub_to_WorldOpts(p_opts);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  LubStatus status = lub_phys2d_world(vm_lub_context, vm_lub_str(p_key),
                                      p_opts ? &n_opts : NULL, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_handle_WorldRef(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_begin(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                          void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef *p_world = a[0].p;
  VmLub_BeginOpts *p_opts = a[1].p;
  LubBeginOpts n_opts = vm_lub_to_BeginOpts(p_opts);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_phys2d_begin(
      vm_lub_context, (p_world ? (int32_t)p_world->host_value : 0),
      p_opts ? &n_opts : NULL);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_world_info(TvmVm *vm, TvmSlot *a,
                                               TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef *p_world = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubWorldInfo result = {0};
  LubStatus status = lub_phys2d_world_info(
      vm_lub_context, (p_world ? (int32_t)p_world->host_value : 0), &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_from_WorldInfo(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_body(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                         void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef *p_world = a[0].p;
  void *p_key = a[1].p;
  VmLub_BodyDesc *p_desc = a[2].p;
  LubBodyDesc n_desc = vm_lub_to_BodyDesc(p_desc);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  LubStatus status = lub_phys2d_body(
      vm_lub_context, (p_world ? (int32_t)p_world->host_value : 0),
      vm_lub_str(p_key), p_desc ? &n_desc : NULL, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_handle_BodyRef(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_box(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                        void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef *p_body = a[0].p;
  void *p_key = a[1].p;
  VmLub_BoxDesc *p_desc = a[2].p;
  LubBoxDesc n_desc = vm_lub_to_BoxDesc(p_desc);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  LubStatus status =
      lub_phys2d_box(vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0),
                     vm_lub_str(p_key), p_desc ? &n_desc : NULL, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_handle_ShapeRef(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_circle(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                           void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef *p_body = a[0].p;
  void *p_key = a[1].p;
  VmLub_CircleDesc *p_desc = a[2].p;
  LubCircleDesc n_desc = vm_lub_to_CircleDesc(p_desc);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  LubStatus status = lub_phys2d_circle(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0),
      vm_lub_str(p_key), p_desc ? &n_desc : NULL, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_handle_ShapeRef(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_capsule(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                            void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef *p_body = a[0].p;
  void *p_key = a[1].p;
  VmLub_CapsuleDesc *p_desc = a[2].p;
  LubCapsuleDesc n_desc = vm_lub_to_CapsuleDesc(p_desc);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  LubStatus status = lub_phys2d_capsule(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0),
      vm_lub_str(p_key), p_desc ? &n_desc : NULL, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_handle_ShapeRef(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_segment(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                            void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef *p_body = a[0].p;
  void *p_key = a[1].p;
  VmLub_SegmentDesc *p_desc = a[2].p;
  LubSegmentDesc n_desc = vm_lub_to_SegmentDesc(p_desc);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  LubStatus status = lub_phys2d_segment(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0),
      vm_lub_str(p_key), p_desc ? &n_desc : NULL, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_handle_ShapeRef(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_polygon(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                            void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef *p_body = a[0].p;
  void *p_key = a[1].p;
  VmLub_PolygonDesc *p_desc = a[2].p;
  LubPolygonDesc n_desc = vm_lub_to_PolygonDesc(p_desc);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  LubStatus status = lub_phys2d_polygon(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0),
      vm_lub_str(p_key), p_desc ? &n_desc : NULL, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_handle_ShapeRef(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_chain(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                          void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef *p_body = a[0].p;
  void *p_key = a[1].p;
  VmLub_ChainDesc *p_desc = a[2].p;
  LubChainDesc n_desc = vm_lub_to_ChainDesc(p_desc);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  LubStatus status = lub_phys2d_chain(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0),
      vm_lub_str(p_key), p_desc ? &n_desc : NULL, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_handle_ChainRef(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_chain_segments(TvmVm *vm, TvmSlot *a,
                                                   TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_ChainRef *p_chain = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  const LubShapeView *result = {0};
  int32_t result_count = 0;
  LubStatus status = lub_phys2d_chain_segments(
      vm_lub_context, (p_chain ? (int32_t)p_chain->host_value : 0), &result,
      &result_count);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND)
               ? NULL
               : vm_lub_from_ShapeView_list(NULL, result, result_count);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_joint(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                          void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef *p_world = a[0].p;
  void *p_key = a[1].p;
  VmLub_JointDesc *p_desc = a[2].p;
  LubJointDesc n_desc = vm_lub_to_JointDesc(p_desc);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  LubStatus status = lub_phys2d_joint(
      vm_lub_context, (p_world ? (int32_t)p_world->host_value : 0),
      vm_lub_str(p_key), p_desc ? &n_desc : NULL, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_handle_JointRef(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_joint_info(TvmVm *vm, TvmSlot *a,
                                               TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_JointRef *p_joint = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubJointInfo result = {0};
  LubStatus status = lub_phys2d_joint_info(
      vm_lub_context, (p_joint ? (int32_t)p_joint->host_value : 0), &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_from_JointInfo(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_joint_force(TvmVm *vm, TvmSlot *a,
                                                TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_JointRef *p_joint = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubVec2d result = {0};
  LubStatus status = lub_phys2d_joint_force(
      vm_lub_context, (p_joint ? (int32_t)p_joint->host_value : 0), &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_from_Vec2d(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_joint_torque(TvmVm *vm, TvmSlot *a,
                                                 TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_JointRef *p_joint = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  float result = {0};
  LubStatus status = lub_phys2d_joint_torque(
      vm_lub_context, (p_joint ? (int32_t)p_joint->host_value : 0), &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].f = result;
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_joint_angle(TvmVm *vm, TvmSlot *a,
                                                TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_JointRef *p_joint = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  float result = {0};
  bool has = false;
  LubStatus status = lub_phys2d_joint_angle(
      vm_lub_context, (p_joint ? (int32_t)p_joint->host_value : 0), &result,
      &has);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  {
    VmLubOptF32 v = (VmLubOptF32){has, result};
    r[0].i = v.has;
    r[1].f = v.v;
  }
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_joint_translation(TvmVm *vm, TvmSlot *a,
                                                      TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_JointRef *p_joint = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  float result = {0};
  bool has = false;
  LubStatus status = lub_phys2d_joint_translation(
      vm_lub_context, (p_joint ? (int32_t)p_joint->host_value : 0), &result,
      &has);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  {
    VmLubOptF32 v = (VmLubOptF32){has, result};
    r[0].i = v.has;
    r[1].f = v.v;
  }
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_joint_speed(TvmVm *vm, TvmSlot *a,
                                                TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_JointRef *p_joint = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  float result = {0};
  bool has = false;
  LubStatus status = lub_phys2d_joint_speed(
      vm_lub_context, (p_joint ? (int32_t)p_joint->host_value : 0), &result,
      &has);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  {
    VmLubOptF32 v = (VmLubOptF32){has, result};
    r[0].i = v.has;
    r[1].f = v.v;
  }
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_joint_length(TvmVm *vm, TvmSlot *a,
                                                 TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_JointRef *p_joint = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  float result = {0};
  bool has = false;
  LubStatus status = lub_phys2d_joint_length(
      vm_lub_context, (p_joint ? (int32_t)p_joint->host_value : 0), &result,
      &has);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  {
    VmLubOptF32 v = (VmLubOptF32){has, result};
    r[0].i = v.has;
    r[1].f = v.v;
  }
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_joint_motor_force(TvmVm *vm, TvmSlot *a,
                                                      TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_JointRef *p_joint = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  float result = {0};
  bool has = false;
  LubStatus status = lub_phys2d_joint_motor_force(
      vm_lub_context, (p_joint ? (int32_t)p_joint->host_value : 0), &result,
      &has);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  {
    VmLubOptF32 v = (VmLubOptF32){has, result};
    r[0].i = v.has;
    r[1].f = v.v;
  }
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_joint_motor_torque(TvmVm *vm, TvmSlot *a,
                                                       TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_JointRef *p_joint = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  float result = {0};
  bool has = false;
  LubStatus status = lub_phys2d_joint_motor_torque(
      vm_lub_context, (p_joint ? (int32_t)p_joint->host_value : 0), &result,
      &has);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  {
    VmLubOptF32 v = (VmLubOptF32){has, result};
    r[0].i = v.has;
    r[1].f = v.v;
  }
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_joint_set_motor(TvmVm *vm, TvmSlot *a,
                                                    TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_JointRef *p_joint = a[0].p;
  VmLub_JointMotorDesc *p_desc = a[1].p;
  LubJointMotorDesc n_desc = vm_lub_to_JointMotorDesc(p_desc);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_phys2d_joint_set_motor(
      vm_lub_context, (p_joint ? (int32_t)p_joint->host_value : 0),
      p_desc ? &n_desc : NULL);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_joint_set_limit(TvmVm *vm, TvmSlot *a,
                                                    TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_JointRef *p_joint = a[0].p;
  VmLub_JointLimitDesc *p_desc = a[1].p;
  LubJointLimitDesc n_desc = vm_lub_to_JointLimitDesc(p_desc);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_phys2d_joint_set_limit(
      vm_lub_context, (p_joint ? (int32_t)p_joint->host_value : 0),
      p_desc ? &n_desc : NULL);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_joint_set_spring(TvmVm *vm, TvmSlot *a,
                                                     TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_JointRef *p_joint = a[0].p;
  VmLub_JointSpringDesc *p_desc = a[1].p;
  LubJointSpringDesc n_desc = vm_lub_to_JointSpringDesc(p_desc);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_phys2d_joint_set_spring(
      vm_lub_context, (p_joint ? (int32_t)p_joint->host_value : 0),
      p_desc ? &n_desc : NULL);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_joint_set_target(TvmVm *vm, TvmSlot *a,
                                                     TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_JointRef *p_joint = a[0].p;
  VmLub_JointTargetDesc *p_desc = a[1].p;
  LubJointTargetDesc n_desc = vm_lub_to_JointTargetDesc(p_desc);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_phys2d_joint_set_target(
      vm_lub_context, (p_joint ? (int32_t)p_joint->host_value : 0),
      p_desc ? &n_desc : NULL);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_step(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                         void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef *p_world = a[0].p;
  float p_dt = a[1].f;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStepInfo result = {0};
  LubStatus status = lub_phys2d_step(
      vm_lub_context, (p_world ? (int32_t)p_world->host_value : 0), p_dt,
      &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_from_StepInfo(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_pose(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                         void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef *p_body = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubPose result = {0};
  LubStatus status = lub_phys2d_pose(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0), &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_from_Pose(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_pose_by_key(TvmVm *vm, TvmSlot *a,
                                                TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef *p_world = a[0].p;
  void *p_key = a[1].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubPose result = {0};
  LubStatus status = lub_phys2d_pose_by_key(
      vm_lub_context, (p_world ? (int32_t)p_world->host_value : 0),
      vm_lub_str(p_key), &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_from_Pose(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_velocity(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                             void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef *p_body = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubVelocity result = {0};
  LubStatus status = lub_phys2d_velocity(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0), &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_from_Velocity(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_mass(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                         void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef *p_body = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubMassData result = {0};
  LubStatus status = lub_phys2d_mass(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0), &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_from_MassData(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_center(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                           void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef *p_body = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubVec2d result = {0};
  LubStatus status = lub_phys2d_center(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0), &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_from_Vec2d(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_world_point(TvmVm *vm, TvmSlot *a,
                                                TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef *p_body = a[0].p;
  VmLub_Vec2d *p_local_point = a[1].p;
  LubVec2d n_local_point = vm_lub_to_Vec2d(p_local_point);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubVec2d result = {0};
  LubStatus status = lub_phys2d_world_point(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0),
      p_local_point ? &n_local_point : NULL, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_from_Vec2d(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_local_point(TvmVm *vm, TvmSlot *a,
                                                TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef *p_body = a[0].p;
  VmLub_Vec2d *p_world_point = a[1].p;
  LubVec2d n_world_point = vm_lub_to_Vec2d(p_world_point);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubVec2d result = {0};
  LubStatus status = lub_phys2d_local_point(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0),
      p_world_point ? &n_world_point : NULL, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_from_Vec2d(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_velocity_at(TvmVm *vm, TvmSlot *a,
                                                TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef *p_body = a[0].p;
  VmLub_Vec2d *p_world_point = a[1].p;
  LubVec2d n_world_point = vm_lub_to_Vec2d(p_world_point);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubVec2d result = {0};
  LubStatus status = lub_phys2d_velocity_at(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0),
      p_world_point ? &n_world_point : NULL, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_from_Vec2d(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_body_shapes(TvmVm *vm, TvmSlot *a,
                                                TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef *p_body = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  const LubShapeView *result = {0};
  int32_t result_count = 0;
  LubStatus status = lub_phys2d_body_shapes(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0), &result,
      &result_count);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND)
               ? NULL
               : vm_lub_from_ShapeView_list(NULL, result, result_count);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_body_joints(TvmVm *vm, TvmSlot *a,
                                                TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef *p_body = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  const LubJointView *result = {0};
  int32_t result_count = 0;
  LubStatus status = lub_phys2d_body_joints(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0), &result,
      &result_count);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND)
               ? NULL
               : vm_lub_from_JointView_list(NULL, result, result_count);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_body_contacts(TvmVm *vm, TvmSlot *a,
                                                  TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef *p_body = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  const LubContactData *result = {0};
  int32_t result_count = 0;
  LubStatus status = lub_phys2d_body_contacts(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0), &result,
      &result_count);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND)
               ? NULL
               : vm_lub_from_ContactData_list(NULL, result, result_count);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_shape_test_point(TvmVm *vm, TvmSlot *a,
                                                     TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_ShapeRef *p_shape = a[0].p;
  VmLub_Vec2d *p_point = a[1].p;
  LubVec2d n_point = vm_lub_to_Vec2d(p_point);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  bool result = {0};
  LubStatus status = lub_phys2d_shape_test_point(
      vm_lub_context, (p_shape ? (int32_t)p_shape->host_value : 0),
      p_point ? &n_point : NULL, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].i = result;
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_shape_raycast(TvmVm *vm, TvmSlot *a,
                                                  TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_ShapeRef *p_shape = a[0].p;
  VmLub_RaycastDesc *p_query = a[1].p;
  LubRaycastDesc n_query = vm_lub_to_RaycastDesc(p_query);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubShapeRayHit result = {0};
  bool has = false;
  LubStatus status = lub_phys2d_shape_raycast(
      vm_lub_context, (p_shape ? (int32_t)p_shape->host_value : 0),
      p_query ? &n_query : NULL, &result, &has);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND || !has) ? NULL
                                             : vm_lub_from_ShapeRayHit(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_shape_closest_point(TvmVm *vm, TvmSlot *a,
                                                        TvmSlot *r,
                                                        void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_ShapeRef *p_shape = a[0].p;
  VmLub_Vec2d *p_point = a[1].p;
  LubVec2d n_point = vm_lub_to_Vec2d(p_point);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubVec2d result = {0};
  LubStatus status = lub_phys2d_shape_closest_point(
      vm_lub_context, (p_shape ? (int32_t)p_shape->host_value : 0),
      p_point ? &n_point : NULL, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_from_Vec2d(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_shape_aabb(TvmVm *vm, TvmSlot *a,
                                               TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_ShapeRef *p_shape = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubAabb result = {0};
  LubStatus status = lub_phys2d_shape_aabb(
      vm_lub_context, (p_shape ? (int32_t)p_shape->host_value : 0), &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_from_Aabb(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_shape_info(TvmVm *vm, TvmSlot *a,
                                               TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_ShapeRef *p_shape = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubShapeInfo result = {0};
  LubStatus status = lub_phys2d_shape_info(
      vm_lub_context, (p_shape ? (int32_t)p_shape->host_value : 0), &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_from_ShapeInfo(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_shape_set_material(TvmVm *vm, TvmSlot *a,
                                                       TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_ShapeRef *p_shape = a[0].p;
  VmLub_MaterialDesc *p_desc = a[1].p;
  LubMaterialDesc n_desc = vm_lub_to_MaterialDesc(p_desc);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_phys2d_shape_set_material(
      vm_lub_context, (p_shape ? (int32_t)p_shape->host_value : 0),
      p_desc ? &n_desc : NULL);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_shape_set_filter(TvmVm *vm, TvmSlot *a,
                                                     TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_ShapeRef *p_shape = a[0].p;
  VmLub_FilterDesc *p_filter = a[1].p;
  LubFilterDesc n_filter = vm_lub_to_FilterDesc(p_filter);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_phys2d_shape_set_filter(
      vm_lub_context, (p_shape ? (int32_t)p_shape->host_value : 0),
      p_filter ? &n_filter : NULL);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_shape_set_events(TvmVm *vm, TvmSlot *a,
                                                     TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_ShapeRef *p_shape = a[0].p;
  VmLub_ShapeEventsDesc *p_desc = a[1].p;
  LubShapeEventsDesc n_desc = vm_lub_to_ShapeEventsDesc(p_desc);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_phys2d_shape_set_events(
      vm_lub_context, (p_shape ? (int32_t)p_shape->host_value : 0),
      p_desc ? &n_desc : NULL);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_contacts(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                             void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef *p_world = a[0].p;
  VmLubOptI32 p_kind = (VmLubOptI32){a[1].i != 0, a[2].i};
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  const LubContactEvent *result = {0};
  int32_t result_count = 0;
  LubStatus status = lub_phys2d_contacts(
      vm_lub_context, (p_world ? (int32_t)p_world->host_value : 0),
      p_kind.has ? &p_kind.v : NULL, &result, &result_count);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND)
               ? NULL
               : vm_lub_from_ContactEvent_list(NULL, result, result_count);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_body_events(TvmVm *vm, TvmSlot *a,
                                                TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef *p_world = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  const LubBodyEvent *result = {0};
  int32_t result_count = 0;
  LubStatus status = lub_phys2d_body_events(
      vm_lub_context, (p_world ? (int32_t)p_world->host_value : 0), &result,
      &result_count);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND)
               ? NULL
               : vm_lub_from_BodyEvent_list(NULL, result, result_count);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_sensors(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                            void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef *p_world = a[0].p;
  VmLubOptI32 p_kind = (VmLubOptI32){a[1].i != 0, a[2].i};
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  const LubSensorEvent *result = {0};
  int32_t result_count = 0;
  LubStatus status = lub_phys2d_sensors(
      vm_lub_context, (p_world ? (int32_t)p_world->host_value : 0),
      p_kind.has ? &p_kind.v : NULL, &result, &result_count);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND)
               ? NULL
               : vm_lub_from_SensorEvent_list(NULL, result, result_count);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_raycast(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                            void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef *p_world = a[0].p;
  VmLub_RaycastDesc *p_query = a[1].p;
  LubRaycastDesc n_query = vm_lub_to_RaycastDesc(p_query);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubRayHit result = {0};
  bool has = false;
  LubStatus status = lub_phys2d_raycast(
      vm_lub_context, (p_world ? (int32_t)p_world->host_value : 0),
      p_query ? &n_query : NULL, &result, &has);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p =
      (status == LUB_NOT_FOUND || !has) ? NULL : vm_lub_from_RayHit(&result);
  VM_LUB_LEAVE(vm);
}
static float vm_lub_lub_phys2d_raycast_all_visitor(void *user,
                                                   const LubRayHit *a) {
  TvmSlot args[1] = {{0}}, rets[1] = {{0}};
  if (vm_lub_error)
    return 0;
  args[0].p = vm_lub_from_RayHit(a);
  if (tvm_invoke(vm_lub_vm, user, args, 1, rets, 1) != TVM_OK) {
    vm_lub_fail_vm();
    return 0;
  }
  return rets[0].f;
}
static TvmStatus vm_host_lub_phys2d_raycast_all(TvmVm *vm, TvmSlot *a,
                                                TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef *p_world = a[0].p;
  VmLub_RaycastDesc *p_query = a[1].p;
  void *p_visitor = a[2].p;
  LubRaycastDesc n_query = vm_lub_to_RaycastDesc(p_query);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  const LubRayHit *result = {0};
  int32_t result_count = 0;
  LubStatus status = lub_phys2d_raycast_all(
      vm_lub_context, (p_world ? (int32_t)p_world->host_value : 0),
      p_query ? &n_query : NULL,
      p_visitor ? vm_lub_lub_phys2d_raycast_all_visitor : NULL, p_visitor,
      &result, &result_count);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND)
               ? NULL
               : vm_lub_from_RayHit_list(NULL, result, result_count);
  VM_LUB_LEAVE(vm);
}
static bool vm_lub_lub_phys2d_overlap_aabb_visitor(void *user,
                                                   const LubShapeView *a) {
  TvmSlot args[1] = {{0}}, rets[1] = {{0}};
  if (vm_lub_error)
    return 0;
  args[0].p = vm_lub_from_ShapeView(a);
  if (tvm_invoke(vm_lub_vm, user, args, 1, rets, 1) != TVM_OK) {
    vm_lub_fail_vm();
    return 0;
  }
  return rets[0].i != 0;
}
static TvmStatus vm_host_lub_phys2d_overlap_aabb(TvmVm *vm, TvmSlot *a,
                                                 TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef *p_world = a[0].p;
  VmLub_AabbDesc *p_query = a[1].p;
  void *p_visitor = a[2].p;
  LubAabbDesc n_query = vm_lub_to_AabbDesc(p_query);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  const LubShapeView *result = {0};
  int32_t result_count = 0;
  LubStatus status = lub_phys2d_overlap_aabb(
      vm_lub_context, (p_world ? (int32_t)p_world->host_value : 0),
      p_query ? &n_query : NULL,
      p_visitor ? vm_lub_lub_phys2d_overlap_aabb_visitor : NULL, p_visitor,
      &result, &result_count);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND)
               ? NULL
               : vm_lub_from_ShapeView_list(NULL, result, result_count);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_shape_cast(TvmVm *vm, TvmSlot *a,
                                               TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef *p_world = a[0].p;
  VmLub_ShapeCastDesc *p_query = a[1].p;
  LubShapeCastDesc n_query = vm_lub_to_ShapeCastDesc(p_query);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubRayHit result = {0};
  bool has = false;
  LubStatus status = lub_phys2d_shape_cast(
      vm_lub_context, (p_world ? (int32_t)p_world->host_value : 0),
      p_query ? &n_query : NULL, &result, &has);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p =
      (status == LUB_NOT_FOUND || !has) ? NULL : vm_lub_from_RayHit(&result);
  VM_LUB_LEAVE(vm);
}
static float vm_lub_lub_phys2d_shape_cast_all_visitor(void *user,
                                                      const LubRayHit *a) {
  TvmSlot args[1] = {{0}}, rets[1] = {{0}};
  if (vm_lub_error)
    return 0;
  args[0].p = vm_lub_from_RayHit(a);
  if (tvm_invoke(vm_lub_vm, user, args, 1, rets, 1) != TVM_OK) {
    vm_lub_fail_vm();
    return 0;
  }
  return rets[0].f;
}
static TvmStatus vm_host_lub_phys2d_shape_cast_all(TvmVm *vm, TvmSlot *a,
                                                   TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef *p_world = a[0].p;
  VmLub_ShapeCastDesc *p_query = a[1].p;
  void *p_visitor = a[2].p;
  LubShapeCastDesc n_query = vm_lub_to_ShapeCastDesc(p_query);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  const LubRayHit *result = {0};
  int32_t result_count = 0;
  LubStatus status = lub_phys2d_shape_cast_all(
      vm_lub_context, (p_world ? (int32_t)p_world->host_value : 0),
      p_query ? &n_query : NULL,
      p_visitor ? vm_lub_lub_phys2d_shape_cast_all_visitor : NULL, p_visitor,
      &result, &result_count);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND)
               ? NULL
               : vm_lub_from_RayHit_list(NULL, result, result_count);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_cast_mover(TvmVm *vm, TvmSlot *a,
                                               TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef *p_world = a[0].p;
  VmLub_MoverDesc *p_query = a[1].p;
  LubMoverDesc n_query = vm_lub_to_MoverDesc(p_query);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubMoverCast result = {0};
  bool has = false;
  LubStatus status = lub_phys2d_cast_mover(
      vm_lub_context, (p_world ? (int32_t)p_world->host_value : 0),
      p_query ? &n_query : NULL, &result, &has);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p =
      (status == LUB_NOT_FOUND || !has) ? NULL : vm_lub_from_MoverCast(&result);
  VM_LUB_LEAVE(vm);
}
static bool vm_lub_lub_phys2d_collide_mover_visitor(void *user,
                                                    const LubMoverPlane *a) {
  TvmSlot args[1] = {{0}}, rets[1] = {{0}};
  if (vm_lub_error)
    return 0;
  args[0].p = vm_lub_from_MoverPlane(a);
  if (tvm_invoke(vm_lub_vm, user, args, 1, rets, 1) != TVM_OK) {
    vm_lub_fail_vm();
    return 0;
  }
  return rets[0].i != 0;
}
static TvmStatus vm_host_lub_phys2d_collide_mover(TvmVm *vm, TvmSlot *a,
                                                  TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef *p_world = a[0].p;
  VmLub_MoverDesc *p_query = a[1].p;
  void *p_visitor = a[2].p;
  LubMoverDesc n_query = vm_lub_to_MoverDesc(p_query);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  const LubMoverPlane *result = {0};
  int32_t result_count = 0;
  LubStatus status = lub_phys2d_collide_mover(
      vm_lub_context, (p_world ? (int32_t)p_world->host_value : 0),
      p_query ? &n_query : NULL,
      p_visitor ? vm_lub_lub_phys2d_collide_mover_visitor : NULL, p_visitor,
      &result, &result_count);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND)
               ? NULL
               : vm_lub_from_MoverPlane_list(NULL, result, result_count);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_explode(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                            void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef *p_world = a[0].p;
  VmLub_ExplosionDesc *p_desc = a[1].p;
  LubExplosionDesc n_desc = vm_lub_to_ExplosionDesc(p_desc);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_phys2d_explode(
      vm_lub_context, (p_world ? (int32_t)p_world->host_value : 0),
      p_desc ? &n_desc : NULL);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_debug(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                          void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef *p_world = a[0].p;
  VmLub_DebugOpts *p_opts = a[1].p;
  LubDebugOpts n_opts = vm_lub_to_DebugOpts(p_opts);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubDebugData result = {0};
  LubStatus status = lub_phys2d_debug(
      vm_lub_context, (p_world ? (int32_t)p_world->host_value : 0),
      p_opts ? &n_opts : NULL, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_from_DebugData(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_profile(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                            void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef *p_world = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubProfile result = {0};
  LubStatus status = lub_phys2d_profile(
      vm_lub_context, (p_world ? (int32_t)p_world->host_value : 0), &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_from_Profile(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_counters(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                             void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef *p_world = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubCounters result = {0};
  LubStatus status = lub_phys2d_counters(
      vm_lub_context, (p_world ? (int32_t)p_world->host_value : 0), &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_from_Counters(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_add_force(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                              void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef *p_body = a[0].p;
  VmLub_Vec2d *p_force = a[1].p;
  VmLub_CommandOpts *p_opts = a[2].p;
  LubVec2d n_force = vm_lub_to_Vec2d(p_force);
  LubCommandOpts n_opts = vm_lub_to_CommandOpts(p_opts);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_phys2d_add_force(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0),
      p_force ? &n_force : NULL, p_opts ? &n_opts : NULL);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_add_force_center(TvmVm *vm, TvmSlot *a,
                                                     TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef *p_body = a[0].p;
  VmLub_Vec2d *p_force = a[1].p;
  VmLub_CommandOpts *p_opts = a[2].p;
  LubVec2d n_force = vm_lub_to_Vec2d(p_force);
  LubCommandOpts n_opts = vm_lub_to_CommandOpts(p_opts);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_phys2d_add_force_center(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0),
      p_force ? &n_force : NULL, p_opts ? &n_opts : NULL);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_add_impulse(TvmVm *vm, TvmSlot *a,
                                                TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef *p_body = a[0].p;
  VmLub_Vec2d *p_impulse = a[1].p;
  VmLub_CommandOpts *p_opts = a[2].p;
  LubVec2d n_impulse = vm_lub_to_Vec2d(p_impulse);
  LubCommandOpts n_opts = vm_lub_to_CommandOpts(p_opts);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_phys2d_add_impulse(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0),
      p_impulse ? &n_impulse : NULL, p_opts ? &n_opts : NULL);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_add_impulse_center(TvmVm *vm, TvmSlot *a,
                                                       TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef *p_body = a[0].p;
  VmLub_Vec2d *p_impulse = a[1].p;
  VmLub_CommandOpts *p_opts = a[2].p;
  LubVec2d n_impulse = vm_lub_to_Vec2d(p_impulse);
  LubCommandOpts n_opts = vm_lub_to_CommandOpts(p_opts);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_phys2d_add_impulse_center(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0),
      p_impulse ? &n_impulse : NULL, p_opts ? &n_opts : NULL);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_add_torque(TvmVm *vm, TvmSlot *a,
                                               TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef *p_body = a[0].p;
  float p_torque = a[1].f;
  VmLub_CommandOpts *p_opts = a[2].p;
  LubCommandOpts n_opts = vm_lub_to_CommandOpts(p_opts);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_phys2d_add_torque(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0), p_torque,
      p_opts ? &n_opts : NULL);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_add_angular_impulse(TvmVm *vm, TvmSlot *a,
                                                        TvmSlot *r,
                                                        void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef *p_body = a[0].p;
  float p_impulse = a[1].f;
  VmLub_CommandOpts *p_opts = a[2].p;
  LubCommandOpts n_opts = vm_lub_to_CommandOpts(p_opts);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_phys2d_add_angular_impulse(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0), p_impulse,
      p_opts ? &n_opts : NULL);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_set_velocity(TvmVm *vm, TvmSlot *a,
                                                 TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef *p_body = a[0].p;
  VmLub_VelocityDesc *p_velocity = a[1].p;
  VmLub_CommandOpts *p_opts = a[2].p;
  LubVelocityDesc n_velocity = vm_lub_to_VelocityDesc(p_velocity);
  LubCommandOpts n_opts = vm_lub_to_CommandOpts(p_opts);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_phys2d_set_velocity(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0),
      p_velocity ? &n_velocity : NULL, p_opts ? &n_opts : NULL);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_teleport(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                             void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef *p_body = a[0].p;
  VmLub_PoseDesc *p_pose = a[1].p;
  VmLub_CommandOpts *p_opts = a[2].p;
  LubPoseDesc n_pose = vm_lub_to_PoseDesc(p_pose);
  LubCommandOpts n_opts = vm_lub_to_CommandOpts(p_opts);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_phys2d_teleport(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0),
      p_pose ? &n_pose : NULL, p_opts ? &n_opts : NULL);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_set_target(TvmVm *vm, TvmSlot *a,
                                               TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef *p_body = a[0].p;
  VmLub_PoseDesc *p_target = a[1].p;
  VmLub_CommandOpts *p_opts = a[2].p;
  LubPoseDesc n_target = vm_lub_to_PoseDesc(p_target);
  LubCommandOpts n_opts = vm_lub_to_CommandOpts(p_opts);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_phys2d_set_target(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0),
      p_target ? &n_target : NULL, p_opts ? &n_opts : NULL);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys2d_set_mass_data(TvmVm *vm, TvmSlot *a,
                                                  TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef *p_body = a[0].p;
  VmLub_MassDataDesc *p_mass_data = a[1].p;
  VmLub_CommandOpts *p_opts = a[2].p;
  LubMassDataDesc n_mass_data = vm_lub_to_MassDataDesc(p_mass_data);
  LubCommandOpts n_opts = vm_lub_to_CommandOpts(p_opts);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_phys2d_set_mass_data(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0),
      p_mass_data ? &n_mass_data : NULL, p_opts ? &n_opts : NULL);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_find_world(TvmVm *vm, TvmSlot *a,
                                               TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_key = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  result = lub_phys3d_find_world(vm_lub_context, vm_lub_str(p_key));
  r[0].p = vm_lub_handle_WorldRef3d(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_find_body(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                              void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef3d *p_world = a[0].p;
  void *p_key = a[1].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  result = lub_phys3d_find_body(vm_lub_context,
                                (p_world ? (int32_t)p_world->host_value : 0),
                                vm_lub_str(p_key));
  r[0].p = vm_lub_handle_BodyRef3d(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_find_shape(TvmVm *vm, TvmSlot *a,
                                               TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef3d *p_body = a[0].p;
  void *p_key = a[1].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  result = lub_phys3d_find_shape(vm_lub_context,
                                 (p_body ? (int32_t)p_body->host_value : 0),
                                 vm_lub_str(p_key));
  r[0].p = vm_lub_handle_ShapeRef3d(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_find_joint(TvmVm *vm, TvmSlot *a,
                                               TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef3d *p_world = a[0].p;
  void *p_key = a[1].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  result = lub_phys3d_find_joint(vm_lub_context,
                                 (p_world ? (int32_t)p_world->host_value : 0),
                                 vm_lub_str(p_key));
  r[0].p = vm_lub_handle_JointRef3d(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_world(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                          void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_key = a[0].p;
  VmLub_WorldOpts3d *p_opts = a[1].p;
  LubWorldOpts3d n_opts = vm_lub_to_WorldOpts3d(p_opts);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  LubStatus status = lub_phys3d_world(vm_lub_context, vm_lub_str(p_key),
                                      p_opts ? &n_opts : NULL, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_handle_WorldRef3d(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_begin(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                          void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef3d *p_world = a[0].p;
  VmLub_BeginOpts3d *p_opts = a[1].p;
  LubBeginOpts3d n_opts = vm_lub_to_BeginOpts3d(p_opts);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_phys3d_begin(
      vm_lub_context, (p_world ? (int32_t)p_world->host_value : 0),
      p_opts ? &n_opts : NULL);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_world_info(TvmVm *vm, TvmSlot *a,
                                               TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef3d *p_world = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubWorldInfo3d result = {0};
  LubStatus status = lub_phys3d_world_info(
      vm_lub_context, (p_world ? (int32_t)p_world->host_value : 0), &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_from_WorldInfo3d(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_body(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                         void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef3d *p_world = a[0].p;
  void *p_key = a[1].p;
  VmLub_BodyDesc3d *p_desc = a[2].p;
  LubBodyDesc3d n_desc = vm_lub_to_BodyDesc3d(p_desc);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  LubStatus status = lub_phys3d_body(
      vm_lub_context, (p_world ? (int32_t)p_world->host_value : 0),
      vm_lub_str(p_key), p_desc ? &n_desc : NULL, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_handle_BodyRef3d(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_sphere(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                           void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef3d *p_body = a[0].p;
  void *p_key = a[1].p;
  VmLub_SphereDesc3d *p_desc = a[2].p;
  LubSphereDesc3d n_desc = vm_lub_to_SphereDesc3d(p_desc);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  LubStatus status = lub_phys3d_sphere(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0),
      vm_lub_str(p_key), p_desc ? &n_desc : NULL, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_handle_ShapeRef3d(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_box(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                        void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef3d *p_body = a[0].p;
  void *p_key = a[1].p;
  VmLub_BoxDesc3d *p_desc = a[2].p;
  LubBoxDesc3d n_desc = vm_lub_to_BoxDesc3d(p_desc);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  LubStatus status =
      lub_phys3d_box(vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0),
                     vm_lub_str(p_key), p_desc ? &n_desc : NULL, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_handle_ShapeRef3d(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_capsule(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                            void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef3d *p_body = a[0].p;
  void *p_key = a[1].p;
  VmLub_CapsuleDesc3d *p_desc = a[2].p;
  LubCapsuleDesc3d n_desc = vm_lub_to_CapsuleDesc3d(p_desc);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  LubStatus status = lub_phys3d_capsule(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0),
      vm_lub_str(p_key), p_desc ? &n_desc : NULL, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_handle_ShapeRef3d(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_cylinder(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                             void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef3d *p_body = a[0].p;
  void *p_key = a[1].p;
  VmLub_CylinderDesc3d *p_desc = a[2].p;
  LubCylinderDesc3d n_desc = vm_lub_to_CylinderDesc3d(p_desc);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  LubStatus status = lub_phys3d_cylinder(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0),
      vm_lub_str(p_key), p_desc ? &n_desc : NULL, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_handle_ShapeRef3d(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_cone(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                         void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef3d *p_body = a[0].p;
  void *p_key = a[1].p;
  VmLub_ConeDesc3d *p_desc = a[2].p;
  LubConeDesc3d n_desc = vm_lub_to_ConeDesc3d(p_desc);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  LubStatus status = lub_phys3d_cone(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0),
      vm_lub_str(p_key), p_desc ? &n_desc : NULL, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_handle_ShapeRef3d(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_hull(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                         void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef3d *p_body = a[0].p;
  void *p_key = a[1].p;
  VmLub_HullDesc3d *p_desc = a[2].p;
  LubHullDesc3d n_desc = vm_lub_to_HullDesc3d(p_desc);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  LubStatus status = lub_phys3d_hull(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0),
      vm_lub_str(p_key), p_desc ? &n_desc : NULL, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_handle_ShapeRef3d(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_mesh(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                         void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef3d *p_body = a[0].p;
  void *p_key = a[1].p;
  VmLub_MeshDesc3d *p_desc = a[2].p;
  LubMeshDesc3d n_desc = vm_lub_to_MeshDesc3d(p_desc);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  LubStatus status = lub_phys3d_mesh(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0),
      vm_lub_str(p_key), p_desc ? &n_desc : NULL, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_handle_ShapeRef3d(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_height_field(TvmVm *vm, TvmSlot *a,
                                                 TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef3d *p_body = a[0].p;
  void *p_key = a[1].p;
  VmLub_HeightFieldDesc3d *p_desc = a[2].p;
  LubHeightFieldDesc3d n_desc = vm_lub_to_HeightFieldDesc3d(p_desc);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  LubStatus status = lub_phys3d_height_field(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0),
      vm_lub_str(p_key), p_desc ? &n_desc : NULL, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_handle_ShapeRef3d(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_compound(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                             void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef3d *p_body = a[0].p;
  void *p_key = a[1].p;
  VmLub_CompoundDesc3d *p_desc = a[2].p;
  LubCompoundDesc3d n_desc = vm_lub_to_CompoundDesc3d(p_desc);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  LubStatus status = lub_phys3d_compound(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0),
      vm_lub_str(p_key), p_desc ? &n_desc : NULL, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_handle_ShapeRef3d(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_joint(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                          void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef3d *p_world = a[0].p;
  void *p_key = a[1].p;
  VmLub_JointDesc3d *p_desc = a[2].p;
  LubJointDesc3d n_desc = vm_lub_to_JointDesc3d(p_desc);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubHandle result = {0};
  LubStatus status = lub_phys3d_joint(
      vm_lub_context, (p_world ? (int32_t)p_world->host_value : 0),
      vm_lub_str(p_key), p_desc ? &n_desc : NULL, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_handle_JointRef3d(result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_joint_info(TvmVm *vm, TvmSlot *a,
                                               TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_JointRef3d *p_joint = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubJointInfo3d result = {0};
  LubStatus status = lub_phys3d_joint_info(
      vm_lub_context, (p_joint ? (int32_t)p_joint->host_value : 0), &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_from_JointInfo3d(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_joint_force(TvmVm *vm, TvmSlot *a,
                                                TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_JointRef3d *p_joint = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubVec3d result = {0};
  LubStatus status = lub_phys3d_joint_force(
      vm_lub_context, (p_joint ? (int32_t)p_joint->host_value : 0), &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_from_Vec3d(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_joint_torque(TvmVm *vm, TvmSlot *a,
                                                 TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_JointRef3d *p_joint = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubVec3d result = {0};
  LubStatus status = lub_phys3d_joint_torque(
      vm_lub_context, (p_joint ? (int32_t)p_joint->host_value : 0), &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_from_Vec3d(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_joint_angle(TvmVm *vm, TvmSlot *a,
                                                TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_JointRef3d *p_joint = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  float result = {0};
  bool has = false;
  LubStatus status = lub_phys3d_joint_angle(
      vm_lub_context, (p_joint ? (int32_t)p_joint->host_value : 0), &result,
      &has);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  {
    VmLubOptF32 v = (VmLubOptF32){has, result};
    r[0].i = v.has;
    r[1].f = v.v;
  }
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_joint_translation(TvmVm *vm, TvmSlot *a,
                                                      TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_JointRef3d *p_joint = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  float result = {0};
  bool has = false;
  LubStatus status = lub_phys3d_joint_translation(
      vm_lub_context, (p_joint ? (int32_t)p_joint->host_value : 0), &result,
      &has);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  {
    VmLubOptF32 v = (VmLubOptF32){has, result};
    r[0].i = v.has;
    r[1].f = v.v;
  }
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_joint_speed(TvmVm *vm, TvmSlot *a,
                                                TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_JointRef3d *p_joint = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  float result = {0};
  bool has = false;
  LubStatus status = lub_phys3d_joint_speed(
      vm_lub_context, (p_joint ? (int32_t)p_joint->host_value : 0), &result,
      &has);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  {
    VmLubOptF32 v = (VmLubOptF32){has, result};
    r[0].i = v.has;
    r[1].f = v.v;
  }
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_joint_length(TvmVm *vm, TvmSlot *a,
                                                 TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_JointRef3d *p_joint = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  float result = {0};
  bool has = false;
  LubStatus status = lub_phys3d_joint_length(
      vm_lub_context, (p_joint ? (int32_t)p_joint->host_value : 0), &result,
      &has);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  {
    VmLubOptF32 v = (VmLubOptF32){has, result};
    r[0].i = v.has;
    r[1].f = v.v;
  }
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_joint_motor_force(TvmVm *vm, TvmSlot *a,
                                                      TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_JointRef3d *p_joint = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  float result = {0};
  bool has = false;
  LubStatus status = lub_phys3d_joint_motor_force(
      vm_lub_context, (p_joint ? (int32_t)p_joint->host_value : 0), &result,
      &has);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  {
    VmLubOptF32 v = (VmLubOptF32){has, result};
    r[0].i = v.has;
    r[1].f = v.v;
  }
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_joint_motor_torque(TvmVm *vm, TvmSlot *a,
                                                       TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_JointRef3d *p_joint = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  float result = {0};
  bool has = false;
  LubStatus status = lub_phys3d_joint_motor_torque(
      vm_lub_context, (p_joint ? (int32_t)p_joint->host_value : 0), &result,
      &has);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  {
    VmLubOptF32 v = (VmLubOptF32){has, result};
    r[0].i = v.has;
    r[1].f = v.v;
  }
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_joint_motor_torque_vector(TvmVm *vm,
                                                              TvmSlot *a,
                                                              TvmSlot *r,
                                                              void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_JointRef3d *p_joint = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubVec3d result = {0};
  bool has = false;
  LubStatus status = lub_phys3d_joint_motor_torque_vector(
      vm_lub_context, (p_joint ? (int32_t)p_joint->host_value : 0), &result,
      &has);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p =
      (status == LUB_NOT_FOUND || !has) ? NULL : vm_lub_from_Vec3d(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_joint_set_motor(TvmVm *vm, TvmSlot *a,
                                                    TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_JointRef3d *p_joint = a[0].p;
  VmLub_JointMotorDesc3d *p_desc = a[1].p;
  LubJointMotorDesc3d n_desc = vm_lub_to_JointMotorDesc3d(p_desc);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_phys3d_joint_set_motor(
      vm_lub_context, (p_joint ? (int32_t)p_joint->host_value : 0),
      p_desc ? &n_desc : NULL);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_joint_set_limit(TvmVm *vm, TvmSlot *a,
                                                    TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_JointRef3d *p_joint = a[0].p;
  VmLub_JointLimitDesc3d *p_desc = a[1].p;
  LubJointLimitDesc3d n_desc = vm_lub_to_JointLimitDesc3d(p_desc);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_phys3d_joint_set_limit(
      vm_lub_context, (p_joint ? (int32_t)p_joint->host_value : 0),
      p_desc ? &n_desc : NULL);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_joint_set_spring(TvmVm *vm, TvmSlot *a,
                                                     TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_JointRef3d *p_joint = a[0].p;
  VmLub_JointSpringDesc3d *p_desc = a[1].p;
  LubJointSpringDesc3d n_desc = vm_lub_to_JointSpringDesc3d(p_desc);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_phys3d_joint_set_spring(
      vm_lub_context, (p_joint ? (int32_t)p_joint->host_value : 0),
      p_desc ? &n_desc : NULL);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_joint_set_target(TvmVm *vm, TvmSlot *a,
                                                     TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_JointRef3d *p_joint = a[0].p;
  VmLub_JointTargetDesc3d *p_desc = a[1].p;
  LubJointTargetDesc3d n_desc = vm_lub_to_JointTargetDesc3d(p_desc);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_phys3d_joint_set_target(
      vm_lub_context, (p_joint ? (int32_t)p_joint->host_value : 0),
      p_desc ? &n_desc : NULL);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_body_joints(TvmVm *vm, TvmSlot *a,
                                                TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef3d *p_body = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  const LubJointView3d *result = {0};
  int32_t result_count = 0;
  LubStatus status = lub_phys3d_body_joints(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0), &result,
      &result_count);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND)
               ? NULL
               : vm_lub_from_JointView3d_list(NULL, result, result_count);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_cast_mover(TvmVm *vm, TvmSlot *a,
                                               TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef3d *p_world = a[0].p;
  VmLub_MoverDesc3d *p_query = a[1].p;
  LubMoverDesc3d n_query = vm_lub_to_MoverDesc3d(p_query);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubMoverCast3d result = {0};
  bool has = false;
  LubStatus status = lub_phys3d_cast_mover(
      vm_lub_context, (p_world ? (int32_t)p_world->host_value : 0),
      p_query ? &n_query : NULL, &result, &has);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND || !has) ? NULL
                                             : vm_lub_from_MoverCast3d(&result);
  VM_LUB_LEAVE(vm);
}
static bool vm_lub_lub_phys3d_collide_mover_visitor(void *user,
                                                    const LubMoverPlane3d *a) {
  TvmSlot args[1] = {{0}}, rets[1] = {{0}};
  if (vm_lub_error)
    return 0;
  args[0].p = vm_lub_from_MoverPlane3d(a);
  if (tvm_invoke(vm_lub_vm, user, args, 1, rets, 1) != TVM_OK) {
    vm_lub_fail_vm();
    return 0;
  }
  return rets[0].i != 0;
}
static TvmStatus vm_host_lub_phys3d_collide_mover(TvmVm *vm, TvmSlot *a,
                                                  TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef3d *p_world = a[0].p;
  VmLub_MoverDesc3d *p_query = a[1].p;
  void *p_visitor = a[2].p;
  LubMoverDesc3d n_query = vm_lub_to_MoverDesc3d(p_query);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  const LubMoverPlane3d *result = {0};
  int32_t result_count = 0;
  LubStatus status = lub_phys3d_collide_mover(
      vm_lub_context, (p_world ? (int32_t)p_world->host_value : 0),
      p_query ? &n_query : NULL,
      p_visitor ? vm_lub_lub_phys3d_collide_mover_visitor : NULL, p_visitor,
      &result, &result_count);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND)
               ? NULL
               : vm_lub_from_MoverPlane3d_list(NULL, result, result_count);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_step(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                         void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef3d *p_world = a[0].p;
  float p_dt = a[1].f;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStepInfo3d result = {0};
  LubStatus status = lub_phys3d_step(
      vm_lub_context, (p_world ? (int32_t)p_world->host_value : 0), p_dt,
      &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_from_StepInfo3d(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_pose(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                         void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef3d *p_body = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubPose3d result = {0};
  LubStatus status = lub_phys3d_pose(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0), &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_from_Pose3d(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_pose_by_key(TvmVm *vm, TvmSlot *a,
                                                TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef3d *p_world = a[0].p;
  void *p_key = a[1].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubPose3d result = {0};
  LubStatus status = lub_phys3d_pose_by_key(
      vm_lub_context, (p_world ? (int32_t)p_world->host_value : 0),
      vm_lub_str(p_key), &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_from_Pose3d(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_velocity(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                             void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef3d *p_body = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubVelocity3d result = {0};
  LubStatus status = lub_phys3d_velocity(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0), &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_from_Velocity3d(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_mass(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                         void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef3d *p_body = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubMassData3d result = {0};
  LubStatus status = lub_phys3d_mass(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0), &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_from_MassData3d(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_center(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                           void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef3d *p_body = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubVec3d result = {0};
  LubStatus status = lub_phys3d_center(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0), &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_from_Vec3d(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_world_point(TvmVm *vm, TvmSlot *a,
                                                TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef3d *p_body = a[0].p;
  VmLub_Vec3d *p_local_point = a[1].p;
  LubVec3d n_local_point = vm_lub_to_Vec3d(p_local_point);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubVec3d result = {0};
  LubStatus status = lub_phys3d_world_point(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0),
      p_local_point ? &n_local_point : NULL, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_from_Vec3d(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_local_point(TvmVm *vm, TvmSlot *a,
                                                TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef3d *p_body = a[0].p;
  VmLub_Vec3d *p_world_point = a[1].p;
  LubVec3d n_world_point = vm_lub_to_Vec3d(p_world_point);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubVec3d result = {0};
  LubStatus status = lub_phys3d_local_point(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0),
      p_world_point ? &n_world_point : NULL, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_from_Vec3d(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_velocity_at(TvmVm *vm, TvmSlot *a,
                                                TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef3d *p_body = a[0].p;
  VmLub_Vec3d *p_world_point = a[1].p;
  LubVec3d n_world_point = vm_lub_to_Vec3d(p_world_point);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubVec3d result = {0};
  LubStatus status = lub_phys3d_velocity_at(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0),
      p_world_point ? &n_world_point : NULL, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_from_Vec3d(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_add_force(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                              void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef3d *p_body = a[0].p;
  VmLub_Vec3d *p_force = a[1].p;
  VmLub_CommandOpts3d *p_opts = a[2].p;
  LubVec3d n_force = vm_lub_to_Vec3d(p_force);
  LubCommandOpts3d n_opts = vm_lub_to_CommandOpts3d(p_opts);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_phys3d_add_force(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0),
      p_force ? &n_force : NULL, p_opts ? &n_opts : NULL);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_add_force_center(TvmVm *vm, TvmSlot *a,
                                                     TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef3d *p_body = a[0].p;
  VmLub_Vec3d *p_force = a[1].p;
  VmLub_CommandOpts3d *p_opts = a[2].p;
  LubVec3d n_force = vm_lub_to_Vec3d(p_force);
  LubCommandOpts3d n_opts = vm_lub_to_CommandOpts3d(p_opts);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_phys3d_add_force_center(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0),
      p_force ? &n_force : NULL, p_opts ? &n_opts : NULL);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_add_impulse(TvmVm *vm, TvmSlot *a,
                                                TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef3d *p_body = a[0].p;
  VmLub_Vec3d *p_impulse = a[1].p;
  VmLub_CommandOpts3d *p_opts = a[2].p;
  LubVec3d n_impulse = vm_lub_to_Vec3d(p_impulse);
  LubCommandOpts3d n_opts = vm_lub_to_CommandOpts3d(p_opts);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_phys3d_add_impulse(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0),
      p_impulse ? &n_impulse : NULL, p_opts ? &n_opts : NULL);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_add_impulse_center(TvmVm *vm, TvmSlot *a,
                                                       TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef3d *p_body = a[0].p;
  VmLub_Vec3d *p_impulse = a[1].p;
  VmLub_CommandOpts3d *p_opts = a[2].p;
  LubVec3d n_impulse = vm_lub_to_Vec3d(p_impulse);
  LubCommandOpts3d n_opts = vm_lub_to_CommandOpts3d(p_opts);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_phys3d_add_impulse_center(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0),
      p_impulse ? &n_impulse : NULL, p_opts ? &n_opts : NULL);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_add_torque(TvmVm *vm, TvmSlot *a,
                                               TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef3d *p_body = a[0].p;
  VmLub_Vec3d *p_torque = a[1].p;
  VmLub_CommandOpts3d *p_opts = a[2].p;
  LubVec3d n_torque = vm_lub_to_Vec3d(p_torque);
  LubCommandOpts3d n_opts = vm_lub_to_CommandOpts3d(p_opts);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_phys3d_add_torque(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0),
      p_torque ? &n_torque : NULL, p_opts ? &n_opts : NULL);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_add_angular_impulse(TvmVm *vm, TvmSlot *a,
                                                        TvmSlot *r,
                                                        void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef3d *p_body = a[0].p;
  VmLub_Vec3d *p_impulse = a[1].p;
  VmLub_CommandOpts3d *p_opts = a[2].p;
  LubVec3d n_impulse = vm_lub_to_Vec3d(p_impulse);
  LubCommandOpts3d n_opts = vm_lub_to_CommandOpts3d(p_opts);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_phys3d_add_angular_impulse(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0),
      p_impulse ? &n_impulse : NULL, p_opts ? &n_opts : NULL);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_set_velocity(TvmVm *vm, TvmSlot *a,
                                                 TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef3d *p_body = a[0].p;
  VmLub_VelocityDesc3d *p_desc = a[1].p;
  LubVelocityDesc3d n_desc = vm_lub_to_VelocityDesc3d(p_desc);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_phys3d_set_velocity(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0),
      p_desc ? &n_desc : NULL);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_teleport(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                             void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef3d *p_body = a[0].p;
  VmLub_PoseDesc3d *p_desc = a[1].p;
  LubPoseDesc3d n_desc = vm_lub_to_PoseDesc3d(p_desc);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_phys3d_teleport(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0),
      p_desc ? &n_desc : NULL);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_set_target(TvmVm *vm, TvmSlot *a,
                                               TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef3d *p_body = a[0].p;
  VmLub_TargetDesc3d *p_desc = a[1].p;
  LubTargetDesc3d n_desc = vm_lub_to_TargetDesc3d(p_desc);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_phys3d_set_target(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0),
      p_desc ? &n_desc : NULL);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_contacts(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                             void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef3d *p_world = a[0].p;
  VmLubOptI32 p_kind = (VmLubOptI32){a[1].i != 0, a[2].i};
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  const LubContactEvent3d *result = {0};
  int32_t result_count = 0;
  LubStatus status = lub_phys3d_contacts(
      vm_lub_context, (p_world ? (int32_t)p_world->host_value : 0),
      p_kind.has ? &p_kind.v : NULL, &result, &result_count);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND)
               ? NULL
               : vm_lub_from_ContactEvent3d_list(NULL, result, result_count);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_body_events(TvmVm *vm, TvmSlot *a,
                                                TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef3d *p_world = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  const LubBodyEvent3d *result = {0};
  int32_t result_count = 0;
  LubStatus status = lub_phys3d_body_events(
      vm_lub_context, (p_world ? (int32_t)p_world->host_value : 0), &result,
      &result_count);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND)
               ? NULL
               : vm_lub_from_BodyEvent3d_list(NULL, result, result_count);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_sensors(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                            void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef3d *p_world = a[0].p;
  VmLubOptI32 p_kind = (VmLubOptI32){a[1].i != 0, a[2].i};
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  const LubSensorEvent3d *result = {0};
  int32_t result_count = 0;
  LubStatus status = lub_phys3d_sensors(
      vm_lub_context, (p_world ? (int32_t)p_world->host_value : 0),
      p_kind.has ? &p_kind.v : NULL, &result, &result_count);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND)
               ? NULL
               : vm_lub_from_SensorEvent3d_list(NULL, result, result_count);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_joint_events(TvmVm *vm, TvmSlot *a,
                                                 TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef3d *p_world = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  const LubJointEvent3d *result = {0};
  int32_t result_count = 0;
  LubStatus status = lub_phys3d_joint_events(
      vm_lub_context, (p_world ? (int32_t)p_world->host_value : 0), &result,
      &result_count);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND)
               ? NULL
               : vm_lub_from_JointEvent3d_list(NULL, result, result_count);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_raycast(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                            void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef3d *p_world = a[0].p;
  VmLub_RaycastDesc3d *p_query = a[1].p;
  LubRaycastDesc3d n_query = vm_lub_to_RaycastDesc3d(p_query);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubRayHit3d result = {0};
  bool has = false;
  LubStatus status = lub_phys3d_raycast(
      vm_lub_context, (p_world ? (int32_t)p_world->host_value : 0),
      p_query ? &n_query : NULL, &result, &has);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p =
      (status == LUB_NOT_FOUND || !has) ? NULL : vm_lub_from_RayHit3d(&result);
  VM_LUB_LEAVE(vm);
}
static float vm_lub_lub_phys3d_raycast_all_visitor(void *user,
                                                   const LubRayHit3d *a) {
  TvmSlot args[1] = {{0}}, rets[1] = {{0}};
  if (vm_lub_error)
    return 0;
  args[0].p = vm_lub_from_RayHit3d(a);
  if (tvm_invoke(vm_lub_vm, user, args, 1, rets, 1) != TVM_OK) {
    vm_lub_fail_vm();
    return 0;
  }
  return rets[0].f;
}
static TvmStatus vm_host_lub_phys3d_raycast_all(TvmVm *vm, TvmSlot *a,
                                                TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef3d *p_world = a[0].p;
  VmLub_RaycastDesc3d *p_query = a[1].p;
  void *p_visitor = a[2].p;
  LubRaycastDesc3d n_query = vm_lub_to_RaycastDesc3d(p_query);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  const LubRayHit3d *result = {0};
  int32_t result_count = 0;
  LubStatus status = lub_phys3d_raycast_all(
      vm_lub_context, (p_world ? (int32_t)p_world->host_value : 0),
      p_query ? &n_query : NULL,
      p_visitor ? vm_lub_lub_phys3d_raycast_all_visitor : NULL, p_visitor,
      &result, &result_count);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND)
               ? NULL
               : vm_lub_from_RayHit3d_list(NULL, result, result_count);
  VM_LUB_LEAVE(vm);
}
static bool vm_lub_lub_phys3d_overlap_aabb_visitor(void *user,
                                                   const LubShapeView3d *a) {
  TvmSlot args[1] = {{0}}, rets[1] = {{0}};
  if (vm_lub_error)
    return 0;
  args[0].p = vm_lub_from_ShapeView3d(a);
  if (tvm_invoke(vm_lub_vm, user, args, 1, rets, 1) != TVM_OK) {
    vm_lub_fail_vm();
    return 0;
  }
  return rets[0].i != 0;
}
static TvmStatus vm_host_lub_phys3d_overlap_aabb(TvmVm *vm, TvmSlot *a,
                                                 TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef3d *p_world = a[0].p;
  VmLub_AabbDesc3d *p_query = a[1].p;
  void *p_visitor = a[2].p;
  LubAabbDesc3d n_query = vm_lub_to_AabbDesc3d(p_query);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  const LubShapeView3d *result = {0};
  int32_t result_count = 0;
  LubStatus status = lub_phys3d_overlap_aabb(
      vm_lub_context, (p_world ? (int32_t)p_world->host_value : 0),
      p_query ? &n_query : NULL,
      p_visitor ? vm_lub_lub_phys3d_overlap_aabb_visitor : NULL, p_visitor,
      &result, &result_count);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND)
               ? NULL
               : vm_lub_from_ShapeView3d_list(NULL, result, result_count);
  VM_LUB_LEAVE(vm);
}
static bool vm_lub_lub_phys3d_overlap_shape_visitor(void *user,
                                                    const LubShapeView3d *a) {
  TvmSlot args[1] = {{0}}, rets[1] = {{0}};
  if (vm_lub_error)
    return 0;
  args[0].p = vm_lub_from_ShapeView3d(a);
  if (tvm_invoke(vm_lub_vm, user, args, 1, rets, 1) != TVM_OK) {
    vm_lub_fail_vm();
    return 0;
  }
  return rets[0].i != 0;
}
static TvmStatus vm_host_lub_phys3d_overlap_shape(TvmVm *vm, TvmSlot *a,
                                                  TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef3d *p_world = a[0].p;
  VmLub_ShapeProxyDesc3d *p_query = a[1].p;
  void *p_visitor = a[2].p;
  LubShapeProxyDesc3d n_query = vm_lub_to_ShapeProxyDesc3d(p_query);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  const LubShapeView3d *result = {0};
  int32_t result_count = 0;
  LubStatus status = lub_phys3d_overlap_shape(
      vm_lub_context, (p_world ? (int32_t)p_world->host_value : 0),
      p_query ? &n_query : NULL,
      p_visitor ? vm_lub_lub_phys3d_overlap_shape_visitor : NULL, p_visitor,
      &result, &result_count);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND)
               ? NULL
               : vm_lub_from_ShapeView3d_list(NULL, result, result_count);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_shape_cast(TvmVm *vm, TvmSlot *a,
                                               TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef3d *p_world = a[0].p;
  VmLub_ShapeProxyDesc3d *p_query = a[1].p;
  LubShapeProxyDesc3d n_query = vm_lub_to_ShapeProxyDesc3d(p_query);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubRayHit3d result = {0};
  bool has = false;
  LubStatus status = lub_phys3d_shape_cast(
      vm_lub_context, (p_world ? (int32_t)p_world->host_value : 0),
      p_query ? &n_query : NULL, &result, &has);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p =
      (status == LUB_NOT_FOUND || !has) ? NULL : vm_lub_from_RayHit3d(&result);
  VM_LUB_LEAVE(vm);
}
static float vm_lub_lub_phys3d_shape_cast_all_visitor(void *user,
                                                      const LubRayHit3d *a) {
  TvmSlot args[1] = {{0}}, rets[1] = {{0}};
  if (vm_lub_error)
    return 0;
  args[0].p = vm_lub_from_RayHit3d(a);
  if (tvm_invoke(vm_lub_vm, user, args, 1, rets, 1) != TVM_OK) {
    vm_lub_fail_vm();
    return 0;
  }
  return rets[0].f;
}
static TvmStatus vm_host_lub_phys3d_shape_cast_all(TvmVm *vm, TvmSlot *a,
                                                   TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef3d *p_world = a[0].p;
  VmLub_ShapeProxyDesc3d *p_query = a[1].p;
  void *p_visitor = a[2].p;
  LubShapeProxyDesc3d n_query = vm_lub_to_ShapeProxyDesc3d(p_query);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  const LubRayHit3d *result = {0};
  int32_t result_count = 0;
  LubStatus status = lub_phys3d_shape_cast_all(
      vm_lub_context, (p_world ? (int32_t)p_world->host_value : 0),
      p_query ? &n_query : NULL,
      p_visitor ? vm_lub_lub_phys3d_shape_cast_all_visitor : NULL, p_visitor,
      &result, &result_count);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND)
               ? NULL
               : vm_lub_from_RayHit3d_list(NULL, result, result_count);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_body_shapes(TvmVm *vm, TvmSlot *a,
                                                TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef3d *p_body = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  const LubShapeView3d *result = {0};
  int32_t result_count = 0;
  LubStatus status = lub_phys3d_body_shapes(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0), &result,
      &result_count);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND)
               ? NULL
               : vm_lub_from_ShapeView3d_list(NULL, result, result_count);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_body_contacts(TvmVm *vm, TvmSlot *a,
                                                  TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_BodyRef3d *p_body = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  const LubContactData3d *result = {0};
  int32_t result_count = 0;
  LubStatus status = lub_phys3d_body_contacts(
      vm_lub_context, (p_body ? (int32_t)p_body->host_value : 0), &result,
      &result_count);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND)
               ? NULL
               : vm_lub_from_ContactData3d_list(NULL, result, result_count);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_shape_raycast(TvmVm *vm, TvmSlot *a,
                                                  TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_ShapeRef3d *p_shape = a[0].p;
  VmLub_RaycastDesc3d *p_query = a[1].p;
  LubRaycastDesc3d n_query = vm_lub_to_RaycastDesc3d(p_query);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubShapeRayHit3d result = {0};
  bool has = false;
  LubStatus status = lub_phys3d_shape_raycast(
      vm_lub_context, (p_shape ? (int32_t)p_shape->host_value : 0),
      p_query ? &n_query : NULL, &result, &has);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND || !has)
               ? NULL
               : vm_lub_from_ShapeRayHit3d(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_shape_closest_point(TvmVm *vm, TvmSlot *a,
                                                        TvmSlot *r,
                                                        void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_ShapeRef3d *p_shape = a[0].p;
  VmLub_Vec3d *p_point = a[1].p;
  LubVec3d n_point = vm_lub_to_Vec3d(p_point);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubVec3d result = {0};
  LubStatus status = lub_phys3d_shape_closest_point(
      vm_lub_context, (p_shape ? (int32_t)p_shape->host_value : 0),
      p_point ? &n_point : NULL, &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_from_Vec3d(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_shape_aabb(TvmVm *vm, TvmSlot *a,
                                               TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_ShapeRef3d *p_shape = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubAabb3d result = {0};
  LubStatus status = lub_phys3d_shape_aabb(
      vm_lub_context, (p_shape ? (int32_t)p_shape->host_value : 0), &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_from_Aabb3d(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_shape_info(TvmVm *vm, TvmSlot *a,
                                               TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_ShapeRef3d *p_shape = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubShapeInfo3d result = {0};
  LubStatus status = lub_phys3d_shape_info(
      vm_lub_context, (p_shape ? (int32_t)p_shape->host_value : 0), &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_from_ShapeInfo3d(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_shape_set_material(TvmVm *vm, TvmSlot *a,
                                                       TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_ShapeRef3d *p_shape = a[0].p;
  VmLub_MaterialDesc3d *p_desc = a[1].p;
  LubMaterialDesc3d n_desc = vm_lub_to_MaterialDesc3d(p_desc);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_phys3d_shape_set_material(
      vm_lub_context, (p_shape ? (int32_t)p_shape->host_value : 0),
      p_desc ? &n_desc : NULL);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_shape_set_filter(TvmVm *vm, TvmSlot *a,
                                                     TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_ShapeRef3d *p_shape = a[0].p;
  VmLub_FilterDesc3d *p_filter = a[1].p;
  LubFilterDesc3d n_filter = vm_lub_to_FilterDesc3d(p_filter);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_phys3d_shape_set_filter(
      vm_lub_context, (p_shape ? (int32_t)p_shape->host_value : 0),
      p_filter ? &n_filter : NULL);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_shape_set_events(TvmVm *vm, TvmSlot *a,
                                                     TvmSlot *r, void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_ShapeRef3d *p_shape = a[0].p;
  VmLub_ShapeEventsDesc3d *p_desc = a[1].p;
  LubShapeEventsDesc3d n_desc = vm_lub_to_ShapeEventsDesc3d(p_desc);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_phys3d_shape_set_events(
      vm_lub_context, (p_shape ? (int32_t)p_shape->host_value : 0),
      p_desc ? &n_desc : NULL);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_profile(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                            void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef3d *p_world = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubProfile3d result = {0};
  LubStatus status = lub_phys3d_profile(
      vm_lub_context, (p_world ? (int32_t)p_world->host_value : 0), &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_from_Profile3d(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_phys3d_counters(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                             void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_WorldRef3d *p_world = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubCounters3d result = {0};
  LubStatus status = lub_phys3d_counters(
      vm_lub_context, (p_world ? (int32_t)p_world->host_value : 0), &result);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = (status == LUB_NOT_FOUND) ? NULL : vm_lub_from_Counters3d(&result);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_png_load(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                      void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_path = a[0].p;
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubView n_bytes = {0};
  int32_t n_width = {0};
  int32_t n_height = {0};
  int32_t n_format = {0};
  int32_t n_stride = {0};
  int32_t n_version = {0};
  int32_t n_status = {0};
  LubStr n_error = {0};
  LubStatus status = lub_png_load(vm_lub_context, vm_lub_str(p_path), &n_bytes,
                                  &n_width, &n_height, &n_format, &n_stride,
                                  &n_version, &n_status, &n_error);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = vm_lub_view_Bytes(n_bytes);
  r[1].i = n_width;
  r[2].i = n_height;
  r[3].i = n_format;
  r[4].i = n_stride;
  r[5].i = n_version;
  r[6].i = n_status;
  r[7].p = vm_lub_string(n_error);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lub_png_write(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                       void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  void *p_path = a[0].p;
  VmLub_Bytes *p_bytes = a[1].p;
  int32_t p_width = a[2].i;
  int32_t p_height = a[3].i;
  VmLubOptI32 p_stride = (VmLubOptI32){a[4].i != 0, a[5].i};
  LubView n_bytes = {0};
  if (p_bytes)
    n_bytes = vm_lub_view_load(p_bytes->host_value);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubStatus status = lub_png_write(vm_lub_context, vm_lub_str(p_path),
                                   n_bytes.ptr, n_bytes.len, p_width, p_height,
                                   p_stride.has ? &p_stride.v : NULL);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  VM_LUB_LEAVE(vm);
}
static TvmStatus vm_host_lubx_vorbis_decode(TvmVm *vm, TvmSlot *a, TvmSlot *r,
                                            void *user) {
  (void)a;
  (void)r;
  (void)user;
  VM_LUB_ENTER();
  VmLub_Bytes *p_data = a[0].p;
  LubView n_data = {0};
  if (p_data)
    n_data = vm_lub_view_load(p_data->host_value);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  LubView n_pcm = {0};
  int32_t n_channels = {0};
  int32_t n_rate = {0};
  LubStatus status = lubx_vorbis_decode(vm_lub_context, n_data.ptr, n_data.len,
                                        &n_pcm, &n_channels, &n_rate);
  vm_lub_check(status);
  if (vm_lub_error)
    VM_LUB_LEAVE(vm);
  r[0].p = vm_lub_view_Bytes(n_pcm);
  r[1].i = n_channels;
  r[2].i = n_rate;
  VM_LUB_LEAVE(vm);
}
/* EventData を VM の object にする (src/vm_host.c の OnEvent) */
static void *vm_lub_event(const LubEventData *e) {
  return vm_lub_from_EventData(e);
}
static const struct {
  const char *name, *params, *rets;
  TvmHostFn fn;
} vm_lub_hosts[] = {
    {"lub.bytes.get", "oi", "i", vm_host_lub_bytes_get},
    {"lub.gfx.main_tex", "", "o", vm_host_lub_gfx_main_tex},
    {"lub.xr.active", "", "b", vm_host_lub_xr_active},
    {"lub.xr.focused", "", "b", vm_host_lub_xr_focused},
    {"lub.xr.view", "iff", "o", vm_host_lub_xr_view},
    {"lub.xr.input", "i", "o", vm_host_lub_xr_input},
    {"lub.app.config", "o", "", vm_host_lub_app_config},
    {"lub.app.quit", "i", "", vm_host_lub_app_quit},
    {"lub.gfx.begin_pass", "o", "", vm_host_lub_gfx_begin_pass},
    {"lub.gfx.end_pass", "", "", vm_host_lub_gfx_end_pass},
    {"lub.gfx.use_shader", "sssbi", "o", vm_host_lub_gfx_use_shader},
    {"lub.gfx.use_shader_compute", "ssbi", "o",
     vm_host_lub_gfx_use_shader_compute},
    {"lub.gfx.use_buffer", "siobi", "o", vm_host_lub_gfx_use_buffer},
    {"lub.gfx.use_buffer_array", "sioibi", "o",
     vm_host_lub_gfx_use_buffer_array},
    {"lub.gfx.use_buffer_ints", "siobi", "o", vm_host_lub_gfx_use_buffer_ints},
    {"lub.gfx.use_buffer_empty", "siibi", "o",
     vm_host_lub_gfx_use_buffer_empty},
    {"lub.gfx.use_texture", "siiiobio", "o", vm_host_lub_gfx_use_texture},
    {"lub.gfx.use_texture_bytes", "siiiobio", "o",
     vm_host_lub_gfx_use_texture_bytes},
    {"lub.gfx.frame_buffer", "io", "o", vm_host_lub_gfx_frame_buffer},
    {"lub.gfx.frame_buffer_array", "ioi", "o",
     vm_host_lub_gfx_frame_buffer_array},
    {"lub.gfx.frame_buffer_ints", "io", "o", vm_host_lub_gfx_frame_buffer_ints},
    {"lub.gfx.lookup_texture", "s", "o", vm_host_lub_gfx_lookup_texture},
    {"lub.gfx.lookup_shader", "s", "o", vm_host_lub_gfx_lookup_shader},
    {"lub.gfx.lookup_buffer", "s", "o", vm_host_lub_gfx_lookup_buffer},
    {"lub.gfx.resource_info", "i", "bsi", vm_host_lub_gfx_resource_info},
    {"lub.gfx.readback", "s", "o", vm_host_lub_gfx_readback},
    {"lub.gfx.read_texture", "oobi", "ioiiiiiis", vm_host_lub_gfx_read_texture},
    {"lub.gfx.draw", "iooi", "", vm_host_lub_gfx_draw},
    {"lub.gfx.bindings_set_texture", "oso", "",
     vm_host_lub_gfx_bindings_set_texture},
    {"lub.gfx.bindings_set_buffer", "oso", "",
     vm_host_lub_gfx_bindings_set_buffer},
    {"lub.gfx.bindings_set_frame_range", "oso", "",
     vm_host_lub_gfx_bindings_set_frame_range},
    {"lub.gfx.bindings_set_floats", "oso", "",
     vm_host_lub_gfx_bindings_set_floats},
    {"lub.gfx.bindings_set4", "osiffff", "", vm_host_lub_gfx_bindings_set4},
    {"lub.gfx.bindings_set16", "osffffffffffffffff", "",
     vm_host_lub_gfx_bindings_set16},
    {"lub.gfx.bindings_set_float_array", "oso", "",
     vm_host_lub_gfx_bindings_set_float_array},
    {"lub.gfx.bindings_clear", "o", "", vm_host_lub_gfx_bindings_clear},
    {"lub.gfx.dispatch", "iiioo", "", vm_host_lub_gfx_dispatch},
    {"lub.gfx.size", "", "ii", vm_host_lub_gfx_size},
    {"lub.input.key_down", "s", "b", vm_host_lub_input_key_down},
    {"lub.input.key_pressed", "s", "b", vm_host_lub_input_key_pressed},
    {"lub.input.key_released", "s", "b", vm_host_lub_input_key_released},
    {"lub.input.mouse_down", "bi", "b", vm_host_lub_input_mouse_down},
    {"lub.input.mouse_pressed", "bi", "b", vm_host_lub_input_mouse_pressed},
    {"lub.input.mouse_released", "bi", "b", vm_host_lub_input_mouse_released},
    {"lub.input.mouse_pos", "", "ff", vm_host_lub_input_mouse_pos},
    {"lub.input.mouse_delta", "", "ff", vm_host_lub_input_mouse_delta},
    {"lub.input.focused", "", "b", vm_host_lub_input_focused},
    {"lub.input.capture_mouse", "b", "b", vm_host_lub_input_capture_mouse},
    {"lub.input.gamepad_connected", "i", "b",
     vm_host_lub_input_gamepad_connected},
    {"lub.input.gamepad_axis", "ii", "f", vm_host_lub_input_gamepad_axis},
    {"lub.input.gamepad_down", "ii", "b", vm_host_lub_input_gamepad_down},
    {"lub.input.gamepad_pressed", "ii", "b", vm_host_lub_input_gamepad_pressed},
    {"lub.input.gamepad_released", "ii", "b",
     vm_host_lub_input_gamepad_released},
    {"lub.io.load_text", "s", "siis", vm_host_lub_io_load_text},
    {"lub.io.save_text", "ss", "", vm_host_lub_io_save_text},
    {"lub.io.load_bytes", "s", "oiis", vm_host_lub_io_load_bytes},
    {"lub.io.load_floats", "s", "oiis", vm_host_lub_io_load_floats},
    {"lub.io.load_gltf", "s", "oiis", vm_host_lub_io_load_gltf},
    {"lub.io.interleave_pn", "o", "o", vm_host_lub_io_interleave_pn},
    {"lub.io.interleave_pncm", "o", "o", vm_host_lub_io_interleave_pncm},
    {"lub.io.interleave_pncmw", "o", "o", vm_host_lub_io_interleave_pncmw},
    {"lub.io.interleave_pnu", "o", "o", vm_host_lub_io_interleave_pnu},
    {"lub.io.interleave_pnut", "o", "o", vm_host_lub_io_interleave_pnut},
    {"lub.mesh.surface_nets", "oiiibfbfbfbf", "o",
     vm_host_lub_mesh_surface_nets},
    {"lub.mesh.sdf_mesh", "oiibf", "o", vm_host_lub_mesh_sdf_mesh},
    {"lub.font.metrics", "o", "o", vm_host_lub_font_metrics},
    {"lub.font.glyph", "oif", "o", vm_host_lub_font_glyph},
    {"lub.font.glyph_mesh", "oibf", "o", vm_host_lub_font_glyph_mesh},
    {"lub.font.kern", "oii", "f", vm_host_lub_font_kern},
    {"lub.ui.render", "", "", vm_host_lub_ui_render},
    {"lub.ui.begin_window", "s", "b", vm_host_lub_ui_begin_window},
    {"lub.ui.end_window", "", "", vm_host_lub_ui_end_window},
    {"lub.ui.text", "s", "", vm_host_lub_ui_text},
    {"lub.ui.button", "s", "b", vm_host_lub_ui_button},
    {"lub.ui.checkbox", "sb", "b", vm_host_lub_ui_checkbox},
    {"lub.ui.slider_float", "sfff", "f", vm_host_lub_ui_slider_float},
    {"lub.ui.slider_int", "siii", "i", vm_host_lub_ui_slider_int},
    {"lub.ui.drag_float", "sfbfbfbf", "f", vm_host_lub_ui_drag_float},
    {"lub.ui.color_edit3", "sfff", "fff", vm_host_lub_ui_color_edit3},
    {"lub.ui.separator", "", "", vm_host_lub_ui_separator},
    {"lub.ui.same_line", "", "", vm_host_lub_ui_same_line},
    {"lub.ui.tree_node", "sbb", "b", vm_host_lub_ui_tree_node},
    {"lub.ui.tree_pop", "", "", vm_host_lub_ui_tree_pop},
    {"lub.ui.set_next_window", "ffff", "", vm_host_lub_ui_set_next_window},
    {"lub.ui.want_capture_mouse", "", "b", vm_host_lub_ui_want_capture_mouse},
    {"lub.host.available", "", "b", vm_host_lub_host_available},
    {"lub.host.send", "ss", "", vm_host_lub_host_send},
    {"lub.host.poll", "", "ss", vm_host_lub_host_poll},
    {"lub.audio.snd", "soiibi", "bi", vm_host_lub_audio_snd},
    {"lub.audio.snd_bytes", "soiibi", "bi", vm_host_lub_audio_snd_bytes},
    {"lub.audio.decode", "o", "oii", vm_host_lub_audio_decode},
    {"lub.audio.play", "io", "b", vm_host_lub_audio_play},
    {"lub.audio.voice", "sio", "b", vm_host_lub_audio_voice},
    {"lub.audio.master_volume", "f", "", vm_host_lub_audio_master_volume},
    {"lub.audio.info", "", "o", vm_host_lub_audio_info},
    {"lub.sys.is_web", "", "b", vm_host_lub_sys_is_web},
    {"lub.sys.fnv1a64", "s", "i", vm_host_lub_sys_fnv1a64},
    {"lub.sys.actual_fps", "", "f", vm_host_lub_sys_actual_fps},
    {"lub.profiler.enabled", "", "b", vm_host_lub_profiler_enabled},
    {"lub.profiler.begin_scope", "s", "", vm_host_lub_profiler_begin_scope},
    {"lub.profiler.end_scope", "s", "", vm_host_lub_profiler_end_scope},
    {"lub.profiler.reset", "", "", vm_host_lub_profiler_reset},
    {"lub.profiler.report", "s", "", vm_host_lub_profiler_report},
    {"lub.phys2d.find_world", "s", "o", vm_host_lub_phys2d_find_world},
    {"lub.phys2d.find_body", "os", "o", vm_host_lub_phys2d_find_body},
    {"lub.phys2d.find_shape", "os", "o", vm_host_lub_phys2d_find_shape},
    {"lub.phys2d.find_chain", "os", "o", vm_host_lub_phys2d_find_chain},
    {"lub.phys2d.find_joint", "os", "o", vm_host_lub_phys2d_find_joint},
    {"lub.phys2d.world", "so", "o", vm_host_lub_phys2d_world},
    {"lub.phys2d.begin", "oo", "", vm_host_lub_phys2d_begin},
    {"lub.phys2d.world_info", "o", "o", vm_host_lub_phys2d_world_info},
    {"lub.phys2d.body", "oso", "o", vm_host_lub_phys2d_body},
    {"lub.phys2d.box", "oso", "o", vm_host_lub_phys2d_box},
    {"lub.phys2d.circle", "oso", "o", vm_host_lub_phys2d_circle},
    {"lub.phys2d.capsule", "oso", "o", vm_host_lub_phys2d_capsule},
    {"lub.phys2d.segment", "oso", "o", vm_host_lub_phys2d_segment},
    {"lub.phys2d.polygon", "oso", "o", vm_host_lub_phys2d_polygon},
    {"lub.phys2d.chain", "oso", "o", vm_host_lub_phys2d_chain},
    {"lub.phys2d.chain_segments", "o", "o", vm_host_lub_phys2d_chain_segments},
    {"lub.phys2d.joint", "oso", "o", vm_host_lub_phys2d_joint},
    {"lub.phys2d.joint_info", "o", "o", vm_host_lub_phys2d_joint_info},
    {"lub.phys2d.joint_force", "o", "o", vm_host_lub_phys2d_joint_force},
    {"lub.phys2d.joint_torque", "o", "f", vm_host_lub_phys2d_joint_torque},
    {"lub.phys2d.joint_angle", "o", "bf", vm_host_lub_phys2d_joint_angle},
    {"lub.phys2d.joint_translation", "o", "bf",
     vm_host_lub_phys2d_joint_translation},
    {"lub.phys2d.joint_speed", "o", "bf", vm_host_lub_phys2d_joint_speed},
    {"lub.phys2d.joint_length", "o", "bf", vm_host_lub_phys2d_joint_length},
    {"lub.phys2d.joint_motor_force", "o", "bf",
     vm_host_lub_phys2d_joint_motor_force},
    {"lub.phys2d.joint_motor_torque", "o", "bf",
     vm_host_lub_phys2d_joint_motor_torque},
    {"lub.phys2d.joint_set_motor", "oo", "",
     vm_host_lub_phys2d_joint_set_motor},
    {"lub.phys2d.joint_set_limit", "oo", "",
     vm_host_lub_phys2d_joint_set_limit},
    {"lub.phys2d.joint_set_spring", "oo", "",
     vm_host_lub_phys2d_joint_set_spring},
    {"lub.phys2d.joint_set_target", "oo", "",
     vm_host_lub_phys2d_joint_set_target},
    {"lub.phys2d.step", "of", "o", vm_host_lub_phys2d_step},
    {"lub.phys2d.pose", "o", "o", vm_host_lub_phys2d_pose},
    {"lub.phys2d.pose_by_key", "os", "o", vm_host_lub_phys2d_pose_by_key},
    {"lub.phys2d.velocity", "o", "o", vm_host_lub_phys2d_velocity},
    {"lub.phys2d.mass", "o", "o", vm_host_lub_phys2d_mass},
    {"lub.phys2d.center", "o", "o", vm_host_lub_phys2d_center},
    {"lub.phys2d.world_point", "oo", "o", vm_host_lub_phys2d_world_point},
    {"lub.phys2d.local_point", "oo", "o", vm_host_lub_phys2d_local_point},
    {"lub.phys2d.velocity_at", "oo", "o", vm_host_lub_phys2d_velocity_at},
    {"lub.phys2d.body_shapes", "o", "o", vm_host_lub_phys2d_body_shapes},
    {"lub.phys2d.body_joints", "o", "o", vm_host_lub_phys2d_body_joints},
    {"lub.phys2d.body_contacts", "o", "o", vm_host_lub_phys2d_body_contacts},
    {"lub.phys2d.shape_test_point", "oo", "b",
     vm_host_lub_phys2d_shape_test_point},
    {"lub.phys2d.shape_raycast", "oo", "o", vm_host_lub_phys2d_shape_raycast},
    {"lub.phys2d.shape_closest_point", "oo", "o",
     vm_host_lub_phys2d_shape_closest_point},
    {"lub.phys2d.shape_aabb", "o", "o", vm_host_lub_phys2d_shape_aabb},
    {"lub.phys2d.shape_info", "o", "o", vm_host_lub_phys2d_shape_info},
    {"lub.phys2d.shape_set_material", "oo", "",
     vm_host_lub_phys2d_shape_set_material},
    {"lub.phys2d.shape_set_filter", "oo", "",
     vm_host_lub_phys2d_shape_set_filter},
    {"lub.phys2d.shape_set_events", "oo", "",
     vm_host_lub_phys2d_shape_set_events},
    {"lub.phys2d.contacts", "obi", "o", vm_host_lub_phys2d_contacts},
    {"lub.phys2d.body_events", "o", "o", vm_host_lub_phys2d_body_events},
    {"lub.phys2d.sensors", "obi", "o", vm_host_lub_phys2d_sensors},
    {"lub.phys2d.raycast", "oo", "o", vm_host_lub_phys2d_raycast},
    {"lub.phys2d.raycast_all", "ooo", "o", vm_host_lub_phys2d_raycast_all},
    {"lub.phys2d.overlap_aabb", "ooo", "o", vm_host_lub_phys2d_overlap_aabb},
    {"lub.phys2d.shape_cast", "oo", "o", vm_host_lub_phys2d_shape_cast},
    {"lub.phys2d.shape_cast_all", "ooo", "o",
     vm_host_lub_phys2d_shape_cast_all},
    {"lub.phys2d.cast_mover", "oo", "o", vm_host_lub_phys2d_cast_mover},
    {"lub.phys2d.collide_mover", "ooo", "o", vm_host_lub_phys2d_collide_mover},
    {"lub.phys2d.explode", "oo", "", vm_host_lub_phys2d_explode},
    {"lub.phys2d.debug", "oo", "o", vm_host_lub_phys2d_debug},
    {"lub.phys2d.profile", "o", "o", vm_host_lub_phys2d_profile},
    {"lub.phys2d.counters", "o", "o", vm_host_lub_phys2d_counters},
    {"lub.phys2d.add_force", "ooo", "", vm_host_lub_phys2d_add_force},
    {"lub.phys2d.add_force_center", "ooo", "",
     vm_host_lub_phys2d_add_force_center},
    {"lub.phys2d.add_impulse", "ooo", "", vm_host_lub_phys2d_add_impulse},
    {"lub.phys2d.add_impulse_center", "ooo", "",
     vm_host_lub_phys2d_add_impulse_center},
    {"lub.phys2d.add_torque", "ofo", "", vm_host_lub_phys2d_add_torque},
    {"lub.phys2d.add_angular_impulse", "ofo", "",
     vm_host_lub_phys2d_add_angular_impulse},
    {"lub.phys2d.set_velocity", "ooo", "", vm_host_lub_phys2d_set_velocity},
    {"lub.phys2d.teleport", "ooo", "", vm_host_lub_phys2d_teleport},
    {"lub.phys2d.set_target", "ooo", "", vm_host_lub_phys2d_set_target},
    {"lub.phys2d.set_mass_data", "ooo", "", vm_host_lub_phys2d_set_mass_data},
    {"lub.phys3d.find_world", "s", "o", vm_host_lub_phys3d_find_world},
    {"lub.phys3d.find_body", "os", "o", vm_host_lub_phys3d_find_body},
    {"lub.phys3d.find_shape", "os", "o", vm_host_lub_phys3d_find_shape},
    {"lub.phys3d.find_joint", "os", "o", vm_host_lub_phys3d_find_joint},
    {"lub.phys3d.world", "so", "o", vm_host_lub_phys3d_world},
    {"lub.phys3d.begin", "oo", "", vm_host_lub_phys3d_begin},
    {"lub.phys3d.world_info", "o", "o", vm_host_lub_phys3d_world_info},
    {"lub.phys3d.body", "oso", "o", vm_host_lub_phys3d_body},
    {"lub.phys3d.sphere", "oso", "o", vm_host_lub_phys3d_sphere},
    {"lub.phys3d.box", "oso", "o", vm_host_lub_phys3d_box},
    {"lub.phys3d.capsule", "oso", "o", vm_host_lub_phys3d_capsule},
    {"lub.phys3d.cylinder", "oso", "o", vm_host_lub_phys3d_cylinder},
    {"lub.phys3d.cone", "oso", "o", vm_host_lub_phys3d_cone},
    {"lub.phys3d.hull", "oso", "o", vm_host_lub_phys3d_hull},
    {"lub.phys3d.mesh", "oso", "o", vm_host_lub_phys3d_mesh},
    {"lub.phys3d.height_field", "oso", "o", vm_host_lub_phys3d_height_field},
    {"lub.phys3d.compound", "oso", "o", vm_host_lub_phys3d_compound},
    {"lub.phys3d.joint", "oso", "o", vm_host_lub_phys3d_joint},
    {"lub.phys3d.joint_info", "o", "o", vm_host_lub_phys3d_joint_info},
    {"lub.phys3d.joint_force", "o", "o", vm_host_lub_phys3d_joint_force},
    {"lub.phys3d.joint_torque", "o", "o", vm_host_lub_phys3d_joint_torque},
    {"lub.phys3d.joint_angle", "o", "bf", vm_host_lub_phys3d_joint_angle},
    {"lub.phys3d.joint_translation", "o", "bf",
     vm_host_lub_phys3d_joint_translation},
    {"lub.phys3d.joint_speed", "o", "bf", vm_host_lub_phys3d_joint_speed},
    {"lub.phys3d.joint_length", "o", "bf", vm_host_lub_phys3d_joint_length},
    {"lub.phys3d.joint_motor_force", "o", "bf",
     vm_host_lub_phys3d_joint_motor_force},
    {"lub.phys3d.joint_motor_torque", "o", "bf",
     vm_host_lub_phys3d_joint_motor_torque},
    {"lub.phys3d.joint_motor_torque_vector", "o", "o",
     vm_host_lub_phys3d_joint_motor_torque_vector},
    {"lub.phys3d.joint_set_motor", "oo", "",
     vm_host_lub_phys3d_joint_set_motor},
    {"lub.phys3d.joint_set_limit", "oo", "",
     vm_host_lub_phys3d_joint_set_limit},
    {"lub.phys3d.joint_set_spring", "oo", "",
     vm_host_lub_phys3d_joint_set_spring},
    {"lub.phys3d.joint_set_target", "oo", "",
     vm_host_lub_phys3d_joint_set_target},
    {"lub.phys3d.body_joints", "o", "o", vm_host_lub_phys3d_body_joints},
    {"lub.phys3d.cast_mover", "oo", "o", vm_host_lub_phys3d_cast_mover},
    {"lub.phys3d.collide_mover", "ooo", "o", vm_host_lub_phys3d_collide_mover},
    {"lub.phys3d.step", "of", "o", vm_host_lub_phys3d_step},
    {"lub.phys3d.pose", "o", "o", vm_host_lub_phys3d_pose},
    {"lub.phys3d.pose_by_key", "os", "o", vm_host_lub_phys3d_pose_by_key},
    {"lub.phys3d.velocity", "o", "o", vm_host_lub_phys3d_velocity},
    {"lub.phys3d.mass", "o", "o", vm_host_lub_phys3d_mass},
    {"lub.phys3d.center", "o", "o", vm_host_lub_phys3d_center},
    {"lub.phys3d.world_point", "oo", "o", vm_host_lub_phys3d_world_point},
    {"lub.phys3d.local_point", "oo", "o", vm_host_lub_phys3d_local_point},
    {"lub.phys3d.velocity_at", "oo", "o", vm_host_lub_phys3d_velocity_at},
    {"lub.phys3d.add_force", "ooo", "", vm_host_lub_phys3d_add_force},
    {"lub.phys3d.add_force_center", "ooo", "",
     vm_host_lub_phys3d_add_force_center},
    {"lub.phys3d.add_impulse", "ooo", "", vm_host_lub_phys3d_add_impulse},
    {"lub.phys3d.add_impulse_center", "ooo", "",
     vm_host_lub_phys3d_add_impulse_center},
    {"lub.phys3d.add_torque", "ooo", "", vm_host_lub_phys3d_add_torque},
    {"lub.phys3d.add_angular_impulse", "ooo", "",
     vm_host_lub_phys3d_add_angular_impulse},
    {"lub.phys3d.set_velocity", "oo", "", vm_host_lub_phys3d_set_velocity},
    {"lub.phys3d.teleport", "oo", "", vm_host_lub_phys3d_teleport},
    {"lub.phys3d.set_target", "oo", "", vm_host_lub_phys3d_set_target},
    {"lub.phys3d.contacts", "obi", "o", vm_host_lub_phys3d_contacts},
    {"lub.phys3d.body_events", "o", "o", vm_host_lub_phys3d_body_events},
    {"lub.phys3d.sensors", "obi", "o", vm_host_lub_phys3d_sensors},
    {"lub.phys3d.joint_events", "o", "o", vm_host_lub_phys3d_joint_events},
    {"lub.phys3d.raycast", "oo", "o", vm_host_lub_phys3d_raycast},
    {"lub.phys3d.raycast_all", "ooo", "o", vm_host_lub_phys3d_raycast_all},
    {"lub.phys3d.overlap_aabb", "ooo", "o", vm_host_lub_phys3d_overlap_aabb},
    {"lub.phys3d.overlap_shape", "ooo", "o", vm_host_lub_phys3d_overlap_shape},
    {"lub.phys3d.shape_cast", "oo", "o", vm_host_lub_phys3d_shape_cast},
    {"lub.phys3d.shape_cast_all", "ooo", "o",
     vm_host_lub_phys3d_shape_cast_all},
    {"lub.phys3d.body_shapes", "o", "o", vm_host_lub_phys3d_body_shapes},
    {"lub.phys3d.body_contacts", "o", "o", vm_host_lub_phys3d_body_contacts},
    {"lub.phys3d.shape_raycast", "oo", "o", vm_host_lub_phys3d_shape_raycast},
    {"lub.phys3d.shape_closest_point", "oo", "o",
     vm_host_lub_phys3d_shape_closest_point},
    {"lub.phys3d.shape_aabb", "o", "o", vm_host_lub_phys3d_shape_aabb},
    {"lub.phys3d.shape_info", "o", "o", vm_host_lub_phys3d_shape_info},
    {"lub.phys3d.shape_set_material", "oo", "",
     vm_host_lub_phys3d_shape_set_material},
    {"lub.phys3d.shape_set_filter", "oo", "",
     vm_host_lub_phys3d_shape_set_filter},
    {"lub.phys3d.shape_set_events", "oo", "",
     vm_host_lub_phys3d_shape_set_events},
    {"lub.phys3d.profile", "o", "o", vm_host_lub_phys3d_profile},
    {"lub.phys3d.counters", "o", "o", vm_host_lub_phys3d_counters},
    {"lub.png.load", "s", "oiiiiiis", vm_host_lub_png_load},
    {"lub.png.write", "soiibi", "", vm_host_lub_png_write},
    {"lubx.vorbis.decode", "o", "oii", vm_host_lubx_vorbis_decode},
    {NULL, NULL, NULL, NULL},
};
/* host の関数を全部登録する (tvm_load の前)。失敗したら 0 */
static int vm_lub_register(TvmVm *vm) {
  for (int i = 0; vm_lub_hosts[i].name; i++)
    if (!tvm_host(vm, vm_lub_hosts[i].name, vm_lub_hosts[i].params,
                  vm_lub_hosts[i].rets, vm_lub_hosts[i].fn, NULL))
      return 0;
  return 1;
}
