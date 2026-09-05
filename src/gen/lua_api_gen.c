// lub の Lua binding。cs-lib/lub_stub.cs から tools/lub-gen が生成する
// (手で編集しない。再生成: dotnet run --project tools/lub-gen -- lua)。
// C API (include/lub/lub_api.h) への詰め替えだけを持つ。Lua の値の
// 読み書き、sentinel、view、callback の土台は src/lua_gen_support.h。
#include "lua_gen_support.h"
#include <string.h>

static void read_LubPassOpts(lua_State *L, int idx, void *out);
static void read_LubDrawOpts(lua_State *L, int idx, void *out);
static void read_LubDispatchOpts(lua_State *L, int idx, void *out);
static void read_LubTextureOpts(lua_State *L, int idx, void *out);
static void read_LubConfigOpts(lua_State *L, int idx, void *out);
static void read_LubMeshData(lua_State *L, int idx, void *out);
static void read_LubSdfBone(lua_State *L, int idx, void *out);
static void read_LubSdfNodeDesc(lua_State *L, int idx, void *out);
static void read_LubPlayOpts(lua_State *L, int idx, void *out);
static void read_LubVoiceOpts(lua_State *L, int idx, void *out);
static void read_LubVec2d(lua_State *L, int idx, void *out);
static void read_LubInitialState(lua_State *L, int idx, void *out);
static void read_LubWorldCallbacks(lua_State *L, int idx, void *out);
static void read_LubWorldOpts(lua_State *L, int idx, void *out);
static void read_LubBeginOpts(lua_State *L, int idx, void *out);
static void read_LubBodyDesc(lua_State *L, int idx, void *out);
static void read_LubFilterDesc(lua_State *L, int idx, void *out);
static void read_LubShapeDesc(lua_State *L, int idx, void *out);
static void read_LubBoxDesc(lua_State *L, int idx, void *out);
static void read_LubCircleDesc(lua_State *L, int idx, void *out);
static void read_LubCapsuleDesc(lua_State *L, int idx, void *out);
static void read_LubSegmentDesc(lua_State *L, int idx, void *out);
static void read_LubPolygonDesc(lua_State *L, int idx, void *out);
static void read_LubChainMaterial(lua_State *L, int idx, void *out);
static void read_LubChainDesc(lua_State *L, int idx, void *out);
static void read_LubJointSpringDesc(lua_State *L, int idx, void *out);
static void read_LubJointLimitDesc(lua_State *L, int idx, void *out);
static void read_LubJointMotorDesc(lua_State *L, int idx, void *out);
static void read_LubJointTargetDesc(lua_State *L, int idx, void *out);
static void read_LubJointDesc(lua_State *L, int idx, void *out);
static void read_LubCommandOpts(lua_State *L, int idx, void *out);
static void read_LubVelocityDesc(lua_State *L, int idx, void *out);
static void read_LubPoseDesc(lua_State *L, int idx, void *out);
static void read_LubMassDataDesc(lua_State *L, int idx, void *out);
static void read_LubMaterialDesc(lua_State *L, int idx, void *out);
static void read_LubShapeEventsDesc(lua_State *L, int idx, void *out);
static void read_LubRaycastDesc(lua_State *L, int idx, void *out);
static void read_LubAabbDesc(lua_State *L, int idx, void *out);
static void read_LubShapeCastDesc(lua_State *L, int idx, void *out);
static void read_LubMoverDesc(lua_State *L, int idx, void *out);
static void read_LubExplosionDesc(lua_State *L, int idx, void *out);
static void read_LubDebugOpts(lua_State *L, int idx, void *out);
static void read_LubVec3d(lua_State *L, int idx, void *out);
static void read_LubQuat3d(lua_State *L, int idx, void *out);
static void read_LubInitialState3d(lua_State *L, int idx, void *out);
static void read_LubMotionLocks3d(lua_State *L, int idx, void *out);
static void read_LubWorldCallbacks3d(lua_State *L, int idx, void *out);
static void read_LubWorldOpts3d(lua_State *L, int idx, void *out);
static void read_LubBeginOpts3d(lua_State *L, int idx, void *out);
static void read_LubBodyDesc3d(lua_State *L, int idx, void *out);
static void read_LubFilterDesc3d(lua_State *L, int idx, void *out);
static void read_LubShapeDesc3d(lua_State *L, int idx, void *out);
static void read_LubSphereDesc3d(lua_State *L, int idx, void *out);
static void read_LubBoxDesc3d(lua_State *L, int idx, void *out);
static void read_LubCapsuleDesc3d(lua_State *L, int idx, void *out);
static void read_LubCylinderDesc3d(lua_State *L, int idx, void *out);
static void read_LubConeDesc3d(lua_State *L, int idx, void *out);
static void read_LubHullDesc3d(lua_State *L, int idx, void *out);
static void read_LubSurfaceMaterial3d(lua_State *L, int idx, void *out);
static void read_LubMeshDesc3d(lua_State *L, int idx, void *out);
static void read_LubHeightFieldDesc3d(lua_State *L, int idx, void *out);
static void read_LubCompoundSphere3d(lua_State *L, int idx, void *out);
static void read_LubCompoundBox3d(lua_State *L, int idx, void *out);
static void read_LubCompoundCapsule3d(lua_State *L, int idx, void *out);
static void read_LubCompoundChild3d(lua_State *L, int idx, void *out);
static void read_LubCompoundDesc3d(lua_State *L, int idx, void *out);
static void read_LubCommandOpts3d(lua_State *L, int idx, void *out);
static void read_LubVelocityDesc3d(lua_State *L, int idx, void *out);
static void read_LubPoseDesc3d(lua_State *L, int idx, void *out);
static void read_LubTargetDesc3d(lua_State *L, int idx, void *out);
static void read_LubFrameDesc3d(lua_State *L, int idx, void *out);
static void read_LubJointSpringDesc3d(lua_State *L, int idx, void *out);
static void read_LubJointLimitDesc3d(lua_State *L, int idx, void *out);
static void read_LubJointMotorDesc3d(lua_State *L, int idx, void *out);
static void read_LubJointTargetDesc3d(lua_State *L, int idx, void *out);
static void read_LubJointDesc3d(lua_State *L, int idx, void *out);
static void read_LubMaterialDesc3d(lua_State *L, int idx, void *out);
static void read_LubShapeEventsDesc3d(lua_State *L, int idx, void *out);
static void read_LubMoverDesc3d(lua_State *L, int idx, void *out);
static void read_LubRaycastDesc3d(lua_State *L, int idx, void *out);
static void read_LubAabbDesc3d(lua_State *L, int idx, void *out);
static void read_LubSphereProxy3d(lua_State *L, int idx, void *out);
static void read_LubBoxProxy3d(lua_State *L, int idx, void *out);
static void read_LubCapsuleProxy3d(lua_State *L, int idx, void *out);
static void read_LubShapeProxyDesc3d(lua_State *L, int idx, void *out);
static void fill_LubMeshData(lua_State *L, const LubMeshData *v);
static void push_LubMeshData(lua_State *L, const LubMeshData *v);
static void fill_LubSdfBone(lua_State *L, const LubSdfBone *v);
static void push_LubSdfBone(lua_State *L, const LubSdfBone *v);
static void push_list_LubSdfBone(lua_State *L, const LubSdfBone *v, int32_t n);
static void fill_LubGltfMaterial(lua_State *L, const LubGltfMaterial *v);
static void push_LubGltfMaterial(lua_State *L, const LubGltfMaterial *v);
static void fill_LubGltfPrimitive(lua_State *L, const LubGltfPrimitive *v);
static void push_LubGltfPrimitive(lua_State *L, const LubGltfPrimitive *v);
static void push_list_LubGltfPrimitive(lua_State *L, const LubGltfPrimitive *v,
                                       int32_t n);
static void fill_LubGltfMesh(lua_State *L, const LubGltfMesh *v);
static void push_LubGltfMesh(lua_State *L, const LubGltfMesh *v);
static void fill_LubGlyphBitmap(lua_State *L, const LubGlyphBitmap *v);
static void push_LubGlyphBitmap(lua_State *L, const LubGlyphBitmap *v);
static void fill_LubGlyphMesh(lua_State *L, const LubGlyphMesh *v);
static void push_LubGlyphMesh(lua_State *L, const LubGlyphMesh *v);
static void fill_LubFontMetrics(lua_State *L, const LubFontMetrics *v);
static void push_LubFontMetrics(lua_State *L, const LubFontMetrics *v);
static void fill_LubAudioInfo(lua_State *L, const LubAudioInfo *v);
static void push_LubAudioInfo(lua_State *L, const LubAudioInfo *v);
static void fill_LubVec2d(lua_State *L, const LubVec2d *v);
static void push_LubVec2d(lua_State *L, const LubVec2d *v);
static void fill_LubShapeView(lua_State *L, const LubShapeView *v);
static void push_LubShapeView(lua_State *L, const LubShapeView *v);
static void push_list_LubShapeView(lua_State *L, const LubShapeView *v,
                                   int32_t n);
static void fill_LubMaterialView(lua_State *L, const LubMaterialView *v);
static void push_LubMaterialView(lua_State *L, const LubMaterialView *v);
static void fill_LubManifoldPoint(lua_State *L, const LubManifoldPoint *v);
static void push_LubManifoldPoint(lua_State *L, const LubManifoldPoint *v);
static void push_list_LubManifoldPoint(lua_State *L, const LubManifoldPoint *v,
                                       int32_t n);
static void fill_LubPreSolveContact(lua_State *L, const LubPreSolveContact *v);
static void push_LubPreSolveContact(lua_State *L, const LubPreSolveContact *v);
static void fill_LubDebugData(lua_State *L, const LubDebugData *v);
static void push_LubDebugData(lua_State *L, const LubDebugData *v);
static void fill_LubPose(lua_State *L, const LubPose *v);
static void push_LubPose(lua_State *L, const LubPose *v);
static void fill_LubVelocity(lua_State *L, const LubVelocity *v);
static void push_LubVelocity(lua_State *L, const LubVelocity *v);
static void fill_LubMassData(lua_State *L, const LubMassData *v);
static void push_LubMassData(lua_State *L, const LubMassData *v);
static void fill_LubAabb(lua_State *L, const LubAabb *v);
static void push_LubAabb(lua_State *L, const LubAabb *v);
static void fill_LubFilterInfo(lua_State *L, const LubFilterInfo *v);
static void push_LubFilterInfo(lua_State *L, const LubFilterInfo *v);
static void fill_LubShapeInfo(lua_State *L, const LubShapeInfo *v);
static void push_LubShapeInfo(lua_State *L, const LubShapeInfo *v);
static void fill_LubWorldCallbackInfo(lua_State *L,
                                      const LubWorldCallbackInfo *v);
static void push_LubWorldCallbackInfo(lua_State *L,
                                      const LubWorldCallbackInfo *v);
static void fill_LubWorldInfo(lua_State *L, const LubWorldInfo *v);
static void push_LubWorldInfo(lua_State *L, const LubWorldInfo *v);
static void fill_LubStepInfo(lua_State *L, const LubStepInfo *v);
static void push_LubStepInfo(lua_State *L, const LubStepInfo *v);
static void fill_LubJointView(lua_State *L, const LubJointView *v);
static void push_LubJointView(lua_State *L, const LubJointView *v);
static void push_list_LubJointView(lua_State *L, const LubJointView *v,
                                   int32_t n);
static void fill_LubJointInfo(lua_State *L, const LubJointInfo *v);
static void push_LubJointInfo(lua_State *L, const LubJointInfo *v);
static void fill_LubContactData(lua_State *L, const LubContactData *v);
static void push_LubContactData(lua_State *L, const LubContactData *v);
static void push_list_LubContactData(lua_State *L, const LubContactData *v,
                                     int32_t n);
static void fill_LubContactEvent(lua_State *L, const LubContactEvent *v);
static void push_LubContactEvent(lua_State *L, const LubContactEvent *v);
static void push_list_LubContactEvent(lua_State *L, const LubContactEvent *v,
                                      int32_t n);
static void fill_LubSensorEvent(lua_State *L, const LubSensorEvent *v);
static void push_LubSensorEvent(lua_State *L, const LubSensorEvent *v);
static void push_list_LubSensorEvent(lua_State *L, const LubSensorEvent *v,
                                     int32_t n);
static void fill_LubBodyEvent(lua_State *L, const LubBodyEvent *v);
static void push_LubBodyEvent(lua_State *L, const LubBodyEvent *v);
static void push_list_LubBodyEvent(lua_State *L, const LubBodyEvent *v,
                                   int32_t n);
static void fill_LubRayHit(lua_State *L, const LubRayHit *v);
static void push_LubRayHit(lua_State *L, const LubRayHit *v);
static void push_list_LubRayHit(lua_State *L, const LubRayHit *v, int32_t n);
static void fill_LubShapeRayHit(lua_State *L, const LubShapeRayHit *v);
static void push_LubShapeRayHit(lua_State *L, const LubShapeRayHit *v);
static void fill_LubMoverCast(lua_State *L, const LubMoverCast *v);
static void push_LubMoverCast(lua_State *L, const LubMoverCast *v);
static void fill_LubMoverPlane(lua_State *L, const LubMoverPlane *v);
static void push_LubMoverPlane(lua_State *L, const LubMoverPlane *v);
static void push_list_LubMoverPlane(lua_State *L, const LubMoverPlane *v,
                                    int32_t n);
static void fill_LubProfile(lua_State *L, const LubProfile *v);
static void push_LubProfile(lua_State *L, const LubProfile *v);
static void fill_LubCounters(lua_State *L, const LubCounters *v);
static void push_LubCounters(lua_State *L, const LubCounters *v);
static void fill_LubVec3d(lua_State *L, const LubVec3d *v);
static void push_LubVec3d(lua_State *L, const LubVec3d *v);
static void fill_LubShapeView3d(lua_State *L, const LubShapeView3d *v);
static void push_LubShapeView3d(lua_State *L, const LubShapeView3d *v);
static void push_list_LubShapeView3d(lua_State *L, const LubShapeView3d *v,
                                     int32_t n);
static void fill_LubPreSolveContact3d(lua_State *L,
                                      const LubPreSolveContact3d *v);
static void push_LubPreSolveContact3d(lua_State *L,
                                      const LubPreSolveContact3d *v);
static void fill_LubPose3d(lua_State *L, const LubPose3d *v);
static void push_LubPose3d(lua_State *L, const LubPose3d *v);
static void fill_LubVelocity3d(lua_State *L, const LubVelocity3d *v);
static void push_LubVelocity3d(lua_State *L, const LubVelocity3d *v);
static void fill_LubInertia3d(lua_State *L, const LubInertia3d *v);
static void push_LubInertia3d(lua_State *L, const LubInertia3d *v);
static void fill_LubMassData3d(lua_State *L, const LubMassData3d *v);
static void push_LubMassData3d(lua_State *L, const LubMassData3d *v);
static void fill_LubAabb3d(lua_State *L, const LubAabb3d *v);
static void push_LubAabb3d(lua_State *L, const LubAabb3d *v);
static void fill_LubShapeInfo3d(lua_State *L, const LubShapeInfo3d *v);
static void push_LubShapeInfo3d(lua_State *L, const LubShapeInfo3d *v);
static void fill_LubWorldInfo3d(lua_State *L, const LubWorldInfo3d *v);
static void push_LubWorldInfo3d(lua_State *L, const LubWorldInfo3d *v);
static void fill_LubStepInfo3d(lua_State *L, const LubStepInfo3d *v);
static void push_LubStepInfo3d(lua_State *L, const LubStepInfo3d *v);
static void fill_LubFrame3d(lua_State *L, const LubFrame3d *v);
static void push_LubFrame3d(lua_State *L, const LubFrame3d *v);
static void fill_LubJointView3d(lua_State *L, const LubJointView3d *v);
static void push_LubJointView3d(lua_State *L, const LubJointView3d *v);
static void push_list_LubJointView3d(lua_State *L, const LubJointView3d *v,
                                     int32_t n);
static void fill_LubJointInfo3d(lua_State *L, const LubJointInfo3d *v);
static void push_LubJointInfo3d(lua_State *L, const LubJointInfo3d *v);
static void fill_LubContactData3d(lua_State *L, const LubContactData3d *v);
static void push_LubContactData3d(lua_State *L, const LubContactData3d *v);
static void push_list_LubContactData3d(lua_State *L, const LubContactData3d *v,
                                       int32_t n);
static void fill_LubContactEvent3d(lua_State *L, const LubContactEvent3d *v);
static void push_LubContactEvent3d(lua_State *L, const LubContactEvent3d *v);
static void push_list_LubContactEvent3d(lua_State *L,
                                        const LubContactEvent3d *v, int32_t n);
static void fill_LubSensorEvent3d(lua_State *L, const LubSensorEvent3d *v);
static void push_LubSensorEvent3d(lua_State *L, const LubSensorEvent3d *v);
static void push_list_LubSensorEvent3d(lua_State *L, const LubSensorEvent3d *v,
                                       int32_t n);
static void fill_LubBodyEvent3d(lua_State *L, const LubBodyEvent3d *v);
static void push_LubBodyEvent3d(lua_State *L, const LubBodyEvent3d *v);
static void push_list_LubBodyEvent3d(lua_State *L, const LubBodyEvent3d *v,
                                     int32_t n);
static void fill_LubJointEvent3d(lua_State *L, const LubJointEvent3d *v);
static void push_LubJointEvent3d(lua_State *L, const LubJointEvent3d *v);
static void push_list_LubJointEvent3d(lua_State *L, const LubJointEvent3d *v,
                                      int32_t n);
static void fill_LubRayHit3d(lua_State *L, const LubRayHit3d *v);
static void push_LubRayHit3d(lua_State *L, const LubRayHit3d *v);
static void push_list_LubRayHit3d(lua_State *L, const LubRayHit3d *v,
                                  int32_t n);
static void fill_LubShapeRayHit3d(lua_State *L, const LubShapeRayHit3d *v);
static void push_LubShapeRayHit3d(lua_State *L, const LubShapeRayHit3d *v);
static void fill_LubMoverCast3d(lua_State *L, const LubMoverCast3d *v);
static void push_LubMoverCast3d(lua_State *L, const LubMoverCast3d *v);
static void fill_LubMoverPlane3d(lua_State *L, const LubMoverPlane3d *v);
static void push_LubMoverPlane3d(lua_State *L, const LubMoverPlane3d *v);
static void push_list_LubMoverPlane3d(lua_State *L, const LubMoverPlane3d *v,
                                      int32_t n);
static void fill_LubProfile3d(lua_State *L, const LubProfile3d *v);
static void push_LubProfile3d(lua_State *L, const LubProfile3d *v);
static void fill_LubCounters3d(lua_State *L, const LubCounters3d *v);
static void push_LubCounters3d(lua_State *L, const LubCounters3d *v);

static const char *const names_LubGfxReadbackStatus[] = {
    "processing", "ready", "error", "dropped", NULL};
static const int32_t values_LubGfxReadbackStatus[] = {0, 1, 2, 3};
static const char *name_LubGfxReadbackStatus(int32_t v) {
  for (int i = 0; names_LubGfxReadbackStatus[i]; ++i)
    if (values_LubGfxReadbackStatus[i] == v)
      return names_LubGfxReadbackStatus[i];
  return NULL;
}

static const char *const names_LubIoStatus[] = {"pending", "ready", "error",
                                                NULL};
static const int32_t values_LubIoStatus[] = {0, 1, 2};
static const char *name_LubIoStatus(int32_t v) {
  for (int i = 0; names_LubIoStatus[i]; ++i)
    if (values_LubIoStatus[i] == v)
      return names_LubIoStatus[i];
  return NULL;
}

static const char *const names_LubPhys2dShapeKind[] = {
    "box", "circle", "capsule", "segment", "polygon", "chain_segment", NULL};
static const int32_t values_LubPhys2dShapeKind[] = {1, 2, 3, 4, 5, 6};
static const char *name_LubPhys2dShapeKind(int32_t v) {
  for (int i = 0; names_LubPhys2dShapeKind[i]; ++i)
    if (values_LubPhys2dShapeKind[i] == v)
      return names_LubPhys2dShapeKind[i];
  return NULL;
}

static const char *const names_LubPhys2dJointType[] = {
    "distance", "filter", "motor", "mouse", "prismatic",
    "revolute", "weld",   "wheel", NULL};
static const int32_t values_LubPhys2dJointType[] = {1, 2, 3, 4, 5, 6, 7, 8};
static const char *name_LubPhys2dJointType(int32_t v) {
  for (int i = 0; names_LubPhys2dJointType[i]; ++i)
    if (values_LubPhys2dJointType[i] == v)
      return names_LubPhys2dJointType[i];
  return NULL;
}

static const char *const names_LubPhys2dEventKind[] = {"begin", "end", "hit",
                                                       NULL};
static const int32_t values_LubPhys2dEventKind[] = {0, 1, 2};
static const char *const names_LubPhys2dProxyKind[] = {
    "box", "circle", "capsule", "segment", "polygon", NULL};
static const int32_t values_LubPhys2dProxyKind[] = {1, 2, 3, 4, 5};
static const char *const names_LubPhys3dShapeKind[] = {
    "sphere", "box",  "capsule",      "cylinder", "cone",
    "hull",   "mesh", "height_field", "compound", NULL};
static const int32_t values_LubPhys3dShapeKind[] = {1, 2, 3, 4, 5, 6, 7, 8, 9};
static const char *name_LubPhys3dShapeKind(int32_t v) {
  for (int i = 0; names_LubPhys3dShapeKind[i]; ++i)
    if (values_LubPhys3dShapeKind[i] == v)
      return names_LubPhys3dShapeKind[i];
  return NULL;
}

static const char *const names_LubPhys3dJointType[] = {
    "distance", "filter",    "motor", "parallel", "prismatic",
    "revolute", "spherical", "weld",  "wheel",    NULL};
static const int32_t values_LubPhys3dJointType[] = {1, 2, 3, 4, 5, 6, 7, 8, 9};
static const char *name_LubPhys3dJointType(int32_t v) {
  for (int i = 0; names_LubPhys3dJointType[i]; ++i)
    if (values_LubPhys3dJointType[i] == v)
      return names_LubPhys3dJointType[i];
  return NULL;
}

static const char *const names_LubPhys3dEventKind[] = {"begin", "end", "hit",
                                                       NULL};
static const int32_t values_LubPhys3dEventKind[] = {0, 1, 2};
static bool tramp_LubWorldCallbacks_filter(void *user, const LubShapeView *a,
                                           const LubShapeView *b) {
  LgenCallbacks *cb = (LgenCallbacks *)user;
  lua_State *L = cb->L;
  if (!lgen_callbacks_push(cb, 0))
    return true;
  push_LubShapeView(L, a);
  push_LubShapeView(L, b);
  if (!lgen_callbacks_call(cb, 0, 2, 1))
    return true;
  bool r = lua_toboolean(L, -1);
  lua_pop(L, 1);
  return r;
}

static bool tramp_LubWorldCallbacks_pre_solve(void *user,
                                              const LubPreSolveContact *a) {
  LgenCallbacks *cb = (LgenCallbacks *)user;
  lua_State *L = cb->L;
  if (!lgen_callbacks_push(cb, 1))
    return true;
  push_LubPreSolveContact(L, a);
  if (!lgen_callbacks_call(cb, 1, 1, 1))
    return true;
  bool r = lua_toboolean(L, -1);
  lua_pop(L, 1);
  return r;
}

static float tramp_LubWorldCallbacks_friction(void *user,
                                              const LubMaterialView *a,
                                              const LubMaterialView *b) {
  LgenCallbacks *cb = (LgenCallbacks *)user;
  lua_State *L = cb->L;
  if (!lgen_callbacks_push(cb, 2))
    return 1.0f;
  push_LubMaterialView(L, a);
  push_LubMaterialView(L, b);
  if (!lgen_callbacks_call(cb, 2, 2, 1))
    return 1.0f;
  float r = (float)lua_tonumber(L, -1);
  lua_pop(L, 1);
  return r;
}

static float tramp_LubWorldCallbacks_restitution(void *user,
                                                 const LubMaterialView *a,
                                                 const LubMaterialView *b) {
  LgenCallbacks *cb = (LgenCallbacks *)user;
  lua_State *L = cb->L;
  if (!lgen_callbacks_push(cb, 3))
    return 1.0f;
  push_LubMaterialView(L, a);
  push_LubMaterialView(L, b);
  if (!lgen_callbacks_call(cb, 3, 2, 1))
    return 1.0f;
  float r = (float)lua_tonumber(L, -1);
  lua_pop(L, 1);
  return r;
}

static bool tramp_LubWorldCallbacks3d_filter(void *user,
                                             const LubShapeView3d *a,
                                             const LubShapeView3d *b) {
  LgenCallbacks *cb = (LgenCallbacks *)user;
  lua_State *L = cb->L;
  if (!lgen_callbacks_push(cb, 0))
    return true;
  push_LubShapeView3d(L, a);
  push_LubShapeView3d(L, b);
  if (!lgen_callbacks_call(cb, 0, 2, 1))
    return true;
  bool r = lua_toboolean(L, -1);
  lua_pop(L, 1);
  return r;
}

static bool tramp_LubWorldCallbacks3d_pre_solve(void *user,
                                                const LubPreSolveContact3d *a) {
  LgenCallbacks *cb = (LgenCallbacks *)user;
  lua_State *L = cb->L;
  if (!lgen_callbacks_push(cb, 1))
    return true;
  push_LubPreSolveContact3d(L, a);
  if (!lgen_callbacks_call(cb, 1, 1, 1))
    return true;
  bool r = lua_toboolean(L, -1);
  lua_pop(L, 1);
  return r;
}

static float tramp_LubWorldCallbacks3d_friction(void *user,
                                                const LubMaterialView *a,
                                                const LubMaterialView *b) {
  LgenCallbacks *cb = (LgenCallbacks *)user;
  lua_State *L = cb->L;
  if (!lgen_callbacks_push(cb, 2))
    return 1.0f;
  push_LubMaterialView(L, a);
  push_LubMaterialView(L, b);
  if (!lgen_callbacks_call(cb, 2, 2, 1))
    return 1.0f;
  float r = (float)lua_tonumber(L, -1);
  lua_pop(L, 1);
  return r;
}

static float tramp_LubWorldCallbacks3d_restitution(void *user,
                                                   const LubMaterialView *a,
                                                   const LubMaterialView *b) {
  LgenCallbacks *cb = (LgenCallbacks *)user;
  lua_State *L = cb->L;
  if (!lgen_callbacks_push(cb, 3))
    return 1.0f;
  push_LubMaterialView(L, a);
  push_LubMaterialView(L, b);
  if (!lgen_callbacks_call(cb, 3, 2, 1))
    return 1.0f;
  float r = (float)lua_tonumber(L, -1);
  lua_pop(L, 1);
  return r;
}

static void read_LubPassOpts(lua_State *L, int idx, void *out_) {
  LubPassOpts *o = (LubPassOpts *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->target = lgen_ref(L, idx, "target", "texture");
  o->targets = lgen_handles(L, idx, "targets", "texture", &o->targets_count);
  o->depth_target = lgen_ref(L, idx, "depth_target", "texture");
  o->has_clear_color =
      lgen_floats_fixed(L, idx, "clear_color", o->clear_color, 4, NULL);
  o->clear_colors = (const float (*)[4])lgen_float_rows(
      L, idx, "clear_colors", 4, &o->clear_colors_count);
  o->has_clear_depth = lgen_num_opt(L, idx, "clear_depth", &o->clear_depth);
  o->has_load = lgen_int_opt(L, idx, "load", &o->load);
}

static void read_LubDrawOpts(lua_State *L, int idx, void *out_) {
  LubDrawOpts *o = (LubDrawOpts *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->shader = lgen_ref(L, idx, "shader", "shader");
  o->has_blend = lgen_int_opt(L, idx, "blend", &o->blend);
  o->has_cull = lgen_int_opt(L, idx, "cull", &o->cull);
  o->has_primitive = lgen_int_opt(L, idx, "primitive", &o->primitive);
  o->has_depth = lgen_bool_opt(L, idx, "depth", &o->depth);
  o->has_depth_write = lgen_bool_opt(L, idx, "depth_write", &o->depth_write);
  o->has_instance_count =
      lgen_int_opt(L, idx, "instance_count", &o->instance_count);
}

static void read_LubDispatchOpts(lua_State *L, int idx, void *out_) {
  LubDispatchOpts *o = (LubDispatchOpts *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->shader = lgen_ref(L, idx, "shader", "shader");
}

static void read_LubTextureOpts(lua_State *L, int idx, void *out_) {
  LubTextureOpts *o = (LubTextureOpts *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_filter = lgen_int_opt(L, idx, "filter", &o->filter);
  o->has_wrap = lgen_int_opt(L, idx, "wrap", &o->wrap);
  o->has_target = lgen_bool_opt(L, idx, "target", &o->target);
  o->has_storage = lgen_bool_opt(L, idx, "storage", &o->storage);
}

static void read_LubConfigOpts(lua_State *L, int idx, void *out_) {
  LubConfigOpts *o = (LubConfigOpts *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->backend = lgen_str(L, idx, "backend");
  o->has_width = lgen_int_opt(L, idx, "width", &o->width);
  o->has_height = lgen_int_opt(L, idx, "height", &o->height);
  o->has_resource_sweep_after_frames = lgen_int_opt(
      L, idx, "resource_sweep_after_frames", &o->resource_sweep_after_frames);
  o->has_readback_depth =
      lgen_int_opt(L, idx, "readback_depth", &o->readback_depth);
}

static void read_LubMeshData(lua_State *L, int idx, void *out_) {
  LubMeshData *o = (LubMeshData *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->positions = lgen_floats(L, idx, "positions", &o->positions_count);
  o->normals = lgen_floats(L, idx, "normals", &o->normals_count);
  o->indices = lgen_ints(L, idx, "indices", &o->indices_count);
  o->vert_count = lgen_int(L, idx, "vert_count", 0);
  o->index_count = lgen_int(L, idx, "index_count", 0);
  o->uvs = lgen_floats(L, idx, "uvs", &o->uvs_count);
  o->tangents = lgen_floats(L, idx, "tangents", &o->tangents_count);
  o->bounds_min = lgen_floats(L, idx, "bounds_min", &o->bounds_min_count);
  o->bounds_max = lgen_floats(L, idx, "bounds_max", &o->bounds_max_count);
  o->has_cell = lgen_num_opt(L, idx, "cell", &o->cell);
  o->colors = lgen_floats(L, idx, "colors", &o->colors_count);
  o->metal_rough = lgen_floats(L, idx, "metal_rough", &o->metal_rough_count);
  o->joints = lgen_ints(L, idx, "joints", &o->joints_count);
  o->weights = lgen_floats(L, idx, "weights", &o->weights_count);
  o->bones = (const LubSdfBone *)lgen_records(
      L, idx, "bones", sizeof(LubSdfBone), read_LubSdfBone, &o->bones_count);
}

static void read_LubSdfBone(lua_State *L, int idx, void *out_) {
  LubSdfBone *o = (LubSdfBone *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->name = lgen_str(L, idx, "name");
  o->x = lgen_num(L, idx, "x", 0.0f);
  o->y = lgen_num(L, idx, "y", 0.0f);
  o->z = lgen_num(L, idx, "z", 0.0f);
}

static void read_LubSdfNodeDesc(lua_State *L, int idx, void *out_) {
  LubSdfNodeDesc *o = (LubSdfNodeDesc *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->op = lgen_int(L, idx, "op", 0);
  o->a = lgen_int(L, idx, "a", 0);
  o->b = lgen_int(L, idx, "b", 0);
  lgen_floats_fixed(L, idx, "params", o->params, 8, &o->params_count);
  o->name = lgen_str(L, idx, "name");
}

static void read_LubPlayOpts(lua_State *L, int idx, void *out_) {
  LubPlayOpts *o = (LubPlayOpts *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_volume = lgen_num_opt(L, idx, "volume", &o->volume);
  o->has_pitch = lgen_num_opt(L, idx, "pitch", &o->pitch);
  o->has_pan = lgen_num_opt(L, idx, "pan", &o->pan);
}

static void read_LubVoiceOpts(lua_State *L, int idx, void *out_) {
  LubVoiceOpts *o = (LubVoiceOpts *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  read_LubPlayOpts(L, idx, &o->base);
  o->has_loop = lgen_bool_opt(L, idx, "loop", &o->loop);
}

static void read_LubVec2d(lua_State *L, int idx, void *out_) {
  LubVec2d *o = (LubVec2d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->x = lgen_num(L, idx, "x", 0.0f);
  o->y = lgen_num(L, idx, "y", 0.0f);
}

static void read_LubInitialState(lua_State *L, int idx, void *out_) {
  LubInitialState *o = (LubInitialState *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_x = lgen_num_opt(L, idx, "x", &o->x);
  o->has_y = lgen_num_opt(L, idx, "y", &o->y);
  o->has_angle = lgen_num_opt(L, idx, "angle", &o->angle);
  o->has_vx = lgen_num_opt(L, idx, "vx", &o->vx);
  o->has_vy = lgen_num_opt(L, idx, "vy", &o->vy);
  o->has_w = lgen_num_opt(L, idx, "w", &o->w);
  o->has_awake = lgen_bool_opt(L, idx, "awake", &o->awake);
}

static void read_LubWorldCallbacks(lua_State *L, int idx, void *out_) {
  LubWorldCallbacks *o = (LubWorldCallbacks *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  LgenCallbacks *cb = lgen_callbacks_new(L, 4);
  o->user = cb;
  o->user_release = lgen_callbacks_free;
  o->filter = lgen_callbacks_field(L, cb, 0, idx, "filter")
                  ? tramp_LubWorldCallbacks_filter
                  : NULL;
  o->pre_solve = lgen_callbacks_field(L, cb, 1, idx, "pre_solve")
                     ? tramp_LubWorldCallbacks_pre_solve
                     : NULL;
  o->friction = lgen_callbacks_field(L, cb, 2, idx, "friction")
                    ? tramp_LubWorldCallbacks_friction
                    : NULL;
  o->restitution = lgen_callbacks_field(L, cb, 3, idx, "restitution")
                       ? tramp_LubWorldCallbacks_restitution
                       : NULL;
}

static void read_LubWorldOpts(lua_State *L, int idx, void *out_) {
  LubWorldOpts *o = (LubWorldOpts *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_version = lgen_int_opt(L, idx, "version", &o->version);
  if (lgen_has(L, idx, "gravity")) {
    lua_getfield(L, idx, "gravity");
    read_LubVec2d(L, -1, &o->gravity);
    lua_pop(L, 1);
    o->has_gravity = true;
  }
  o->has_fixed_dt = lgen_num_opt(L, idx, "fixed_dt", &o->fixed_dt);
  o->has_substeps = lgen_int_opt(L, idx, "substeps", &o->substeps);
  o->has_max_steps = lgen_int_opt(L, idx, "max_steps", &o->max_steps);
  o->has_sleep = lgen_bool_opt(L, idx, "sleep", &o->sleep);
  o->has_continuous = lgen_bool_opt(L, idx, "continuous", &o->continuous);
  o->has_hit_event_threshold =
      lgen_num_opt(L, idx, "hit_event_threshold", &o->hit_event_threshold);
  if (lgen_has(L, idx, "callbacks")) {
    lua_getfield(L, idx, "callbacks");
    read_LubWorldCallbacks(L, -1, &o->callbacks);
    lua_pop(L, 1);
    o->has_callbacks = true;
  }
}

static void read_LubBeginOpts(lua_State *L, int idx, void *out_) {
  LubBeginOpts *o = (LubBeginOpts *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_prune = lgen_bool_opt(L, idx, "prune", &o->prune);
}

static void read_LubBodyDesc(lua_State *L, int idx, void *out_) {
  LubBodyDesc *o = (LubBodyDesc *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_version = lgen_int_opt(L, idx, "version", &o->version);
  o->has_type = lgen_int_opt(L, idx, "type", &o->type);
  o->has_fixed_rotation =
      lgen_bool_opt(L, idx, "fixed_rotation", &o->fixed_rotation);
  o->has_bullet = lgen_bool_opt(L, idx, "bullet", &o->bullet);
  o->has_enabled = lgen_bool_opt(L, idx, "enabled", &o->enabled);
  o->has_awake = lgen_bool_opt(L, idx, "awake", &o->awake);
  o->has_sleep = lgen_bool_opt(L, idx, "sleep", &o->sleep);
  o->has_sleep_threshold =
      lgen_num_opt(L, idx, "sleep_threshold", &o->sleep_threshold);
  o->has_gravity_scale =
      lgen_num_opt(L, idx, "gravity_scale", &o->gravity_scale);
  o->has_linear_damping =
      lgen_num_opt(L, idx, "linear_damping", &o->linear_damping);
  o->has_angular_damping =
      lgen_num_opt(L, idx, "angular_damping", &o->angular_damping);
  if (lgen_has(L, idx, "initial")) {
    lua_getfield(L, idx, "initial");
    read_LubInitialState(L, -1, &o->initial);
    lua_pop(L, 1);
    o->has_initial = true;
  }
}

static void read_LubFilterDesc(lua_State *L, int idx, void *out_) {
  LubFilterDesc *o = (LubFilterDesc *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_category_bits =
      lgen_bits_opt(L, idx, "category_bits", &o->category_bits);
  o->has_mask_bits = lgen_bits_opt(L, idx, "mask_bits", &o->mask_bits);
  o->has_group = lgen_int_opt(L, idx, "group", &o->group);
}

static void read_LubShapeDesc(lua_State *L, int idx, void *out_) {
  LubShapeDesc *o = (LubShapeDesc *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_version = lgen_int_opt(L, idx, "version", &o->version);
  o->has_density = lgen_num_opt(L, idx, "density", &o->density);
  o->has_friction = lgen_num_opt(L, idx, "friction", &o->friction);
  o->has_restitution = lgen_num_opt(L, idx, "restitution", &o->restitution);
  o->tag = lgen_str(L, idx, "tag");
  o->material_name = lgen_str(L, idx, "material_name");
  o->has_material_id = lgen_int_opt(L, idx, "material_id", &o->material_id);
  o->has_sensor = lgen_bool_opt(L, idx, "sensor", &o->sensor);
  o->has_contact = lgen_bool_opt(L, idx, "contact", &o->contact);
  o->has_hit = lgen_bool_opt(L, idx, "hit", &o->hit);
  o->has_sensor_events =
      lgen_bool_opt(L, idx, "sensor_events", &o->sensor_events);
  o->has_pre_solve = lgen_bool_opt(L, idx, "pre_solve", &o->pre_solve);
  if (lgen_has(L, idx, "filter")) {
    lua_getfield(L, idx, "filter");
    read_LubFilterDesc(L, -1, &o->filter);
    lua_pop(L, 1);
    o->has_filter = true;
  }
}

static void read_LubBoxDesc(lua_State *L, int idx, void *out_) {
  LubBoxDesc *o = (LubBoxDesc *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  read_LubShapeDesc(L, idx, &o->base);
  o->hx = lgen_num(L, idx, "hx", 0.0f);
  o->hy = lgen_num(L, idx, "hy", 0.0f);
  o->has_cx = lgen_num_opt(L, idx, "cx", &o->cx);
  o->has_cy = lgen_num_opt(L, idx, "cy", &o->cy);
  o->has_angle = lgen_num_opt(L, idx, "angle", &o->angle);
}

static void read_LubCircleDesc(lua_State *L, int idx, void *out_) {
  LubCircleDesc *o = (LubCircleDesc *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  read_LubShapeDesc(L, idx, &o->base);
  o->r = lgen_num(L, idx, "r", 0.0f);
  o->has_cx = lgen_num_opt(L, idx, "cx", &o->cx);
  o->has_cy = lgen_num_opt(L, idx, "cy", &o->cy);
}

static void read_LubCapsuleDesc(lua_State *L, int idx, void *out_) {
  LubCapsuleDesc *o = (LubCapsuleDesc *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  read_LubShapeDesc(L, idx, &o->base);
  o->ax = lgen_num(L, idx, "ax", 0.0f);
  o->ay = lgen_num(L, idx, "ay", 0.0f);
  o->bx = lgen_num(L, idx, "bx", 0.0f);
  o->by = lgen_num(L, idx, "by", 0.0f);
  o->r = lgen_num(L, idx, "r", 0.0f);
}

static void read_LubSegmentDesc(lua_State *L, int idx, void *out_) {
  LubSegmentDesc *o = (LubSegmentDesc *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  read_LubShapeDesc(L, idx, &o->base);
  o->ax = lgen_num(L, idx, "ax", 0.0f);
  o->ay = lgen_num(L, idx, "ay", 0.0f);
  o->bx = lgen_num(L, idx, "bx", 0.0f);
  o->by = lgen_num(L, idx, "by", 0.0f);
}

static void read_LubPolygonDesc(lua_State *L, int idx, void *out_) {
  LubPolygonDesc *o = (LubPolygonDesc *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  read_LubShapeDesc(L, idx, &o->base);
  o->points = lgen_floats(L, idx, "points", &o->points_count);
  o->has_radius = lgen_num_opt(L, idx, "radius", &o->radius);
  o->has_cx = lgen_num_opt(L, idx, "cx", &o->cx);
  o->has_cy = lgen_num_opt(L, idx, "cy", &o->cy);
  o->has_angle = lgen_num_opt(L, idx, "angle", &o->angle);
}

static void read_LubChainMaterial(lua_State *L, int idx, void *out_) {
  LubChainMaterial *o = (LubChainMaterial *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_friction = lgen_num_opt(L, idx, "friction", &o->friction);
  o->has_restitution = lgen_num_opt(L, idx, "restitution", &o->restitution);
  o->has_material_id = lgen_int_opt(L, idx, "material_id", &o->material_id);
}

static void read_LubChainDesc(lua_State *L, int idx, void *out_) {
  LubChainDesc *o = (LubChainDesc *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->version = lgen_int(L, idx, "version", 0);
  o->points = lgen_floats(L, idx, "points", &o->points_count);
  o->materials = (const LubChainMaterial *)lgen_records(
      L, idx, "materials", sizeof(LubChainMaterial), read_LubChainMaterial,
      &o->materials_count);
  o->has_loop = lgen_bool_opt(L, idx, "loop", &o->loop);
  o->has_friction = lgen_num_opt(L, idx, "friction", &o->friction);
  o->has_restitution = lgen_num_opt(L, idx, "restitution", &o->restitution);
  o->tag = lgen_str(L, idx, "tag");
  o->material_name = lgen_str(L, idx, "material_name");
  o->has_material_id = lgen_int_opt(L, idx, "material_id", &o->material_id);
  o->has_sensor_events =
      lgen_bool_opt(L, idx, "sensor_events", &o->sensor_events);
  if (lgen_has(L, idx, "filter")) {
    lua_getfield(L, idx, "filter");
    read_LubFilterDesc(L, -1, &o->filter);
    lua_pop(L, 1);
    o->has_filter = true;
  }
}

static void read_LubJointSpringDesc(lua_State *L, int idx, void *out_) {
  LubJointSpringDesc *o = (LubJointSpringDesc *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_enabled = lgen_bool_opt(L, idx, "enabled", &o->enabled);
  o->has_hertz = lgen_num_opt(L, idx, "hertz", &o->hertz);
  o->has_damping_ratio =
      lgen_num_opt(L, idx, "damping_ratio", &o->damping_ratio);
  o->has_linear_hertz = lgen_num_opt(L, idx, "linear_hertz", &o->linear_hertz);
  o->has_linear_damping_ratio =
      lgen_num_opt(L, idx, "linear_damping_ratio", &o->linear_damping_ratio);
  o->has_angular_hertz =
      lgen_num_opt(L, idx, "angular_hertz", &o->angular_hertz);
  o->has_angular_damping_ratio =
      lgen_num_opt(L, idx, "angular_damping_ratio", &o->angular_damping_ratio);
}

static void read_LubJointLimitDesc(lua_State *L, int idx, void *out_) {
  LubJointLimitDesc *o = (LubJointLimitDesc *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_enabled = lgen_bool_opt(L, idx, "enabled", &o->enabled);
  o->has_lower = lgen_num_opt(L, idx, "lower", &o->lower);
  o->has_upper = lgen_num_opt(L, idx, "upper", &o->upper);
  o->has_min_length = lgen_num_opt(L, idx, "min_length", &o->min_length);
  o->has_max_length = lgen_num_opt(L, idx, "max_length", &o->max_length);
}

static void read_LubJointMotorDesc(lua_State *L, int idx, void *out_) {
  LubJointMotorDesc *o = (LubJointMotorDesc *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_enabled = lgen_bool_opt(L, idx, "enabled", &o->enabled);
  o->has_speed = lgen_num_opt(L, idx, "speed", &o->speed);
  o->has_max_force = lgen_num_opt(L, idx, "max_force", &o->max_force);
  o->has_max_torque = lgen_num_opt(L, idx, "max_torque", &o->max_torque);
  if (lgen_has(L, idx, "linear_offset")) {
    lua_getfield(L, idx, "linear_offset");
    read_LubVec2d(L, -1, &o->linear_offset);
    lua_pop(L, 1);
    o->has_linear_offset = true;
  }
  o->has_angular_offset =
      lgen_num_opt(L, idx, "angular_offset", &o->angular_offset);
  o->has_correction_factor =
      lgen_num_opt(L, idx, "correction_factor", &o->correction_factor);
}

static void read_LubJointTargetDesc(lua_State *L, int idx, void *out_) {
  LubJointTargetDesc *o = (LubJointTargetDesc *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_x = lgen_num_opt(L, idx, "x", &o->x);
  o->has_y = lgen_num_opt(L, idx, "y", &o->y);
  o->has_translation = lgen_num_opt(L, idx, "translation", &o->translation);
  o->has_angle = lgen_num_opt(L, idx, "angle", &o->angle);
  if (lgen_has(L, idx, "linear_offset")) {
    lua_getfield(L, idx, "linear_offset");
    read_LubVec2d(L, -1, &o->linear_offset);
    lua_pop(L, 1);
    o->has_linear_offset = true;
  }
  o->has_angular_offset =
      lgen_num_opt(L, idx, "angular_offset", &o->angular_offset);
}

static void read_LubJointDesc(lua_State *L, int idx, void *out_) {
  LubJointDesc *o = (LubJointDesc *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_version = lgen_int_opt(L, idx, "version", &o->version);
  o->type = lgen_enum_str(L, idx, "type", names_LubPhys2dJointType,
                          values_LubPhys2dJointType, "JointType", &o->has_type);
  o->body_a = lgen_ref(L, idx, "body_a", "body");
  o->body_b = lgen_ref(L, idx, "body_b", "body");
  if (lgen_has(L, idx, "anchor_a")) {
    lua_getfield(L, idx, "anchor_a");
    read_LubVec2d(L, -1, &o->anchor_a);
    lua_pop(L, 1);
    o->has_anchor_a = true;
  }
  if (lgen_has(L, idx, "anchor_b")) {
    lua_getfield(L, idx, "anchor_b");
    read_LubVec2d(L, -1, &o->anchor_b);
    lua_pop(L, 1);
    o->has_anchor_b = true;
  }
  if (lgen_has(L, idx, "local_anchor_a")) {
    lua_getfield(L, idx, "local_anchor_a");
    read_LubVec2d(L, -1, &o->local_anchor_a);
    lua_pop(L, 1);
    o->has_local_anchor_a = true;
  }
  if (lgen_has(L, idx, "local_anchor_b")) {
    lua_getfield(L, idx, "local_anchor_b");
    read_LubVec2d(L, -1, &o->local_anchor_b);
    lua_pop(L, 1);
    o->has_local_anchor_b = true;
  }
  if (lgen_has(L, idx, "local_axis_a")) {
    lua_getfield(L, idx, "local_axis_a");
    read_LubVec2d(L, -1, &o->local_axis_a);
    lua_pop(L, 1);
    o->has_local_axis_a = true;
  }
  o->has_reference_angle =
      lgen_num_opt(L, idx, "reference_angle", &o->reference_angle);
  o->has_collide_connected =
      lgen_bool_opt(L, idx, "collide_connected", &o->collide_connected);
  o->has_length = lgen_num_opt(L, idx, "length", &o->length);
  o->has_min_length = lgen_num_opt(L, idx, "min_length", &o->min_length);
  o->has_max_length = lgen_num_opt(L, idx, "max_length", &o->max_length);
  o->has_lower = lgen_num_opt(L, idx, "lower", &o->lower);
  o->has_upper = lgen_num_opt(L, idx, "upper", &o->upper);
  o->has_target_angle = lgen_num_opt(L, idx, "target_angle", &o->target_angle);
  o->has_target_translation =
      lgen_num_opt(L, idx, "target_translation", &o->target_translation);
  if (lgen_has(L, idx, "linear_offset")) {
    lua_getfield(L, idx, "linear_offset");
    read_LubVec2d(L, -1, &o->linear_offset);
    lua_pop(L, 1);
    o->has_linear_offset = true;
  }
  o->has_angular_offset =
      lgen_num_opt(L, idx, "angular_offset", &o->angular_offset);
  o->has_hertz = lgen_num_opt(L, idx, "hertz", &o->hertz);
  o->has_damping_ratio =
      lgen_num_opt(L, idx, "damping_ratio", &o->damping_ratio);
  o->has_max_force = lgen_num_opt(L, idx, "max_force", &o->max_force);
  o->has_max_torque = lgen_num_opt(L, idx, "max_torque", &o->max_torque);
  o->has_motor_speed = lgen_num_opt(L, idx, "motor_speed", &o->motor_speed);
  o->has_correction_factor =
      lgen_num_opt(L, idx, "correction_factor", &o->correction_factor);
  if (lgen_has(L, idx, "spring")) {
    lua_getfield(L, idx, "spring");
    read_LubJointSpringDesc(L, -1, &o->spring);
    lua_pop(L, 1);
    o->has_spring = true;
  }
  if (lgen_has(L, idx, "limit")) {
    lua_getfield(L, idx, "limit");
    read_LubJointLimitDesc(L, -1, &o->limit);
    lua_pop(L, 1);
    o->has_limit = true;
  }
  if (lgen_has(L, idx, "motor")) {
    lua_getfield(L, idx, "motor");
    read_LubJointMotorDesc(L, -1, &o->motor);
    lua_pop(L, 1);
    o->has_motor = true;
  }
  if (lgen_has(L, idx, "target")) {
    lua_getfield(L, idx, "target");
    read_LubVec2d(L, -1, &o->target);
    lua_pop(L, 1);
    o->has_target = true;
  }
}

static void read_LubCommandOpts(lua_State *L, int idx, void *out_) {
  LubCommandOpts *o = (LubCommandOpts *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_wake = lgen_bool_opt(L, idx, "wake", &o->wake);
  if (lgen_has(L, idx, "point")) {
    lua_getfield(L, idx, "point");
    read_LubVec2d(L, -1, &o->point);
    lua_pop(L, 1);
    o->has_point = true;
  }
  o->has_time_step = lgen_num_opt(L, idx, "time_step", &o->time_step);
}

static void read_LubVelocityDesc(lua_State *L, int idx, void *out_) {
  LubVelocityDesc *o = (LubVelocityDesc *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_vx = lgen_num_opt(L, idx, "vx", &o->vx);
  o->has_vy = lgen_num_opt(L, idx, "vy", &o->vy);
  o->has_w = lgen_num_opt(L, idx, "w", &o->w);
}

static void read_LubPoseDesc(lua_State *L, int idx, void *out_) {
  LubPoseDesc *o = (LubPoseDesc *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_x = lgen_num_opt(L, idx, "x", &o->x);
  o->has_y = lgen_num_opt(L, idx, "y", &o->y);
  o->has_angle = lgen_num_opt(L, idx, "angle", &o->angle);
}

static void read_LubMassDataDesc(lua_State *L, int idx, void *out_) {
  LubMassDataDesc *o = (LubMassDataDesc *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_mass = lgen_num_opt(L, idx, "mass", &o->mass);
  o->has_inertia = lgen_num_opt(L, idx, "inertia", &o->inertia);
  if (lgen_has(L, idx, "local_center")) {
    lua_getfield(L, idx, "local_center");
    read_LubVec2d(L, -1, &o->local_center);
    lua_pop(L, 1);
    o->has_local_center = true;
  }
}

static void read_LubMaterialDesc(lua_State *L, int idx, void *out_) {
  LubMaterialDesc *o = (LubMaterialDesc *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_density = lgen_num_opt(L, idx, "density", &o->density);
  o->has_friction = lgen_num_opt(L, idx, "friction", &o->friction);
  o->has_restitution = lgen_num_opt(L, idx, "restitution", &o->restitution);
  o->material_name = lgen_str(L, idx, "material_name");
  o->has_material_id = lgen_int_opt(L, idx, "material_id", &o->material_id);
}

static void read_LubShapeEventsDesc(lua_State *L, int idx, void *out_) {
  LubShapeEventsDesc *o = (LubShapeEventsDesc *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_sensor_events =
      lgen_bool_opt(L, idx, "sensor_events", &o->sensor_events);
  o->has_contact = lgen_bool_opt(L, idx, "contact", &o->contact);
  o->has_pre_solve = lgen_bool_opt(L, idx, "pre_solve", &o->pre_solve);
  o->has_hit = lgen_bool_opt(L, idx, "hit", &o->hit);
}

static void read_LubRaycastDesc(lua_State *L, int idx, void *out_) {
  LubRaycastDesc *o = (LubRaycastDesc *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_x = lgen_num_opt(L, idx, "x", &o->x);
  o->has_y = lgen_num_opt(L, idx, "y", &o->y);
  o->has_dx = lgen_num_opt(L, idx, "dx", &o->dx);
  o->has_dy = lgen_num_opt(L, idx, "dy", &o->dy);
  o->has_max_fraction = lgen_num_opt(L, idx, "max_fraction", &o->max_fraction);
  if (lgen_has(L, idx, "filter")) {
    lua_getfield(L, idx, "filter");
    read_LubFilterDesc(L, -1, &o->filter);
    lua_pop(L, 1);
    o->has_filter = true;
  }
}

static void read_LubAabbDesc(lua_State *L, int idx, void *out_) {
  LubAabbDesc *o = (LubAabbDesc *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->min_x = lgen_num(L, idx, "min_x", 0.0f);
  o->min_y = lgen_num(L, idx, "min_y", 0.0f);
  o->max_x = lgen_num(L, idx, "max_x", 0.0f);
  o->max_y = lgen_num(L, idx, "max_y", 0.0f);
  if (lgen_has(L, idx, "filter")) {
    lua_getfield(L, idx, "filter");
    read_LubFilterDesc(L, -1, &o->filter);
    lua_pop(L, 1);
    o->has_filter = true;
  }
}

static void read_LubShapeCastDesc(lua_State *L, int idx, void *out_) {
  LubShapeCastDesc *o = (LubShapeCastDesc *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->kind = lgen_enum_str(L, idx, "kind", names_LubPhys2dProxyKind,
                          values_LubPhys2dProxyKind, "ProxyKind", &o->has_kind);
  o->has_x = lgen_num_opt(L, idx, "x", &o->x);
  o->has_y = lgen_num_opt(L, idx, "y", &o->y);
  o->has_angle = lgen_num_opt(L, idx, "angle", &o->angle);
  o->has_radius = lgen_num_opt(L, idx, "radius", &o->radius);
  o->has_cx = lgen_num_opt(L, idx, "cx", &o->cx);
  o->has_cy = lgen_num_opt(L, idx, "cy", &o->cy);
  o->has_ax = lgen_num_opt(L, idx, "ax", &o->ax);
  o->has_ay = lgen_num_opt(L, idx, "ay", &o->ay);
  o->has_bx = lgen_num_opt(L, idx, "bx", &o->bx);
  o->has_by = lgen_num_opt(L, idx, "by", &o->by);
  o->has_hx = lgen_num_opt(L, idx, "hx", &o->hx);
  o->has_hy = lgen_num_opt(L, idx, "hy", &o->hy);
  o->points = lgen_floats(L, idx, "points", &o->points_count);
  o->has_dx = lgen_num_opt(L, idx, "dx", &o->dx);
  o->has_dy = lgen_num_opt(L, idx, "dy", &o->dy);
  o->has_max_fraction = lgen_num_opt(L, idx, "max_fraction", &o->max_fraction);
  if (lgen_has(L, idx, "filter")) {
    lua_getfield(L, idx, "filter");
    read_LubFilterDesc(L, -1, &o->filter);
    lua_pop(L, 1);
    o->has_filter = true;
  }
}

static void read_LubMoverDesc(lua_State *L, int idx, void *out_) {
  LubMoverDesc *o = (LubMoverDesc *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->ax = lgen_num(L, idx, "ax", 0.0f);
  o->ay = lgen_num(L, idx, "ay", 0.0f);
  o->bx = lgen_num(L, idx, "bx", 0.0f);
  o->by = lgen_num(L, idx, "by", 0.0f);
  o->r = lgen_num(L, idx, "r", 0.0f);
  o->has_dx = lgen_num_opt(L, idx, "dx", &o->dx);
  o->has_dy = lgen_num_opt(L, idx, "dy", &o->dy);
  o->has_max_fraction = lgen_num_opt(L, idx, "max_fraction", &o->max_fraction);
  if (lgen_has(L, idx, "filter")) {
    lua_getfield(L, idx, "filter");
    read_LubFilterDesc(L, -1, &o->filter);
    lua_pop(L, 1);
    o->has_filter = true;
  }
}

static void read_LubExplosionDesc(lua_State *L, int idx, void *out_) {
  LubExplosionDesc *o = (LubExplosionDesc *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_x = lgen_num_opt(L, idx, "x", &o->x);
  o->has_y = lgen_num_opt(L, idx, "y", &o->y);
  o->has_radius = lgen_num_opt(L, idx, "radius", &o->radius);
  o->has_falloff = lgen_num_opt(L, idx, "falloff", &o->falloff);
  o->has_impulse_per_length =
      lgen_num_opt(L, idx, "impulse_per_length", &o->impulse_per_length);
  if (lgen_has(L, idx, "filter")) {
    lua_getfield(L, idx, "filter");
    read_LubFilterDesc(L, -1, &o->filter);
    lua_pop(L, 1);
    o->has_filter = true;
  }
}

static void read_LubDebugOpts(lua_State *L, int idx, void *out_) {
  LubDebugOpts *o = (LubDebugOpts *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_shapes = lgen_bool_opt(L, idx, "shapes", &o->shapes);
  o->has_joints = lgen_bool_opt(L, idx, "joints", &o->joints);
  o->has_joint_extras = lgen_bool_opt(L, idx, "joint_extras", &o->joint_extras);
  o->has_bounds = lgen_bool_opt(L, idx, "bounds", &o->bounds);
  o->has_mass = lgen_bool_opt(L, idx, "mass", &o->mass);
  o->has_body_names = lgen_bool_opt(L, idx, "body_names", &o->body_names);
  o->has_contacts = lgen_bool_opt(L, idx, "contacts", &o->contacts);
  o->has_graph_colors = lgen_bool_opt(L, idx, "graph_colors", &o->graph_colors);
  o->has_contact_normals =
      lgen_bool_opt(L, idx, "contact_normals", &o->contact_normals);
  o->has_contact_impulses =
      lgen_bool_opt(L, idx, "contact_impulses", &o->contact_impulses);
  o->has_contact_features =
      lgen_bool_opt(L, idx, "contact_features", &o->contact_features);
  o->has_friction_impulses =
      lgen_bool_opt(L, idx, "friction_impulses", &o->friction_impulses);
  o->has_islands = lgen_bool_opt(L, idx, "islands", &o->islands);
  if (lgen_has(L, idx, "drawing_bounds")) {
    lua_getfield(L, idx, "drawing_bounds");
    read_LubAabbDesc(L, -1, &o->drawing_bounds);
    lua_pop(L, 1);
    o->has_drawing_bounds = true;
  }
}

static void read_LubVec3d(lua_State *L, int idx, void *out_) {
  LubVec3d *o = (LubVec3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->x = lgen_num(L, idx, "x", 0.0f);
  o->y = lgen_num(L, idx, "y", 0.0f);
  o->z = lgen_num(L, idx, "z", 0.0f);
}

static void read_LubQuat3d(lua_State *L, int idx, void *out_) {
  LubQuat3d *o = (LubQuat3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->x = lgen_num(L, idx, "x", 0.0f);
  o->y = lgen_num(L, idx, "y", 0.0f);
  o->z = lgen_num(L, idx, "z", 0.0f);
  o->w = lgen_num(L, idx, "w", 0.0f);
}

static void read_LubInitialState3d(lua_State *L, int idx, void *out_) {
  LubInitialState3d *o = (LubInitialState3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_x = lgen_num_opt(L, idx, "x", &o->x);
  o->has_y = lgen_num_opt(L, idx, "y", &o->y);
  o->has_z = lgen_num_opt(L, idx, "z", &o->z);
  if (lgen_has(L, idx, "quat")) {
    lua_getfield(L, idx, "quat");
    read_LubQuat3d(L, -1, &o->quat);
    lua_pop(L, 1);
    o->has_quat = true;
  }
  if (lgen_has(L, idx, "euler")) {
    lua_getfield(L, idx, "euler");
    read_LubVec3d(L, -1, &o->euler);
    lua_pop(L, 1);
    o->has_euler = true;
  }
  o->has_vx = lgen_num_opt(L, idx, "vx", &o->vx);
  o->has_vy = lgen_num_opt(L, idx, "vy", &o->vy);
  o->has_vz = lgen_num_opt(L, idx, "vz", &o->vz);
  o->has_wx = lgen_num_opt(L, idx, "wx", &o->wx);
  o->has_wy = lgen_num_opt(L, idx, "wy", &o->wy);
  o->has_wz = lgen_num_opt(L, idx, "wz", &o->wz);
  o->has_awake = lgen_bool_opt(L, idx, "awake", &o->awake);
}

static void read_LubMotionLocks3d(lua_State *L, int idx, void *out_) {
  LubMotionLocks3d *o = (LubMotionLocks3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_linear_x = lgen_bool_opt(L, idx, "linear_x", &o->linear_x);
  o->has_linear_y = lgen_bool_opt(L, idx, "linear_y", &o->linear_y);
  o->has_linear_z = lgen_bool_opt(L, idx, "linear_z", &o->linear_z);
  o->has_angular_x = lgen_bool_opt(L, idx, "angular_x", &o->angular_x);
  o->has_angular_y = lgen_bool_opt(L, idx, "angular_y", &o->angular_y);
  o->has_angular_z = lgen_bool_opt(L, idx, "angular_z", &o->angular_z);
}

static void read_LubWorldCallbacks3d(lua_State *L, int idx, void *out_) {
  LubWorldCallbacks3d *o = (LubWorldCallbacks3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  LgenCallbacks *cb = lgen_callbacks_new(L, 4);
  o->user = cb;
  o->user_release = lgen_callbacks_free;
  o->filter = lgen_callbacks_field(L, cb, 0, idx, "filter")
                  ? tramp_LubWorldCallbacks3d_filter
                  : NULL;
  o->pre_solve = lgen_callbacks_field(L, cb, 1, idx, "pre_solve")
                     ? tramp_LubWorldCallbacks3d_pre_solve
                     : NULL;
  o->friction = lgen_callbacks_field(L, cb, 2, idx, "friction")
                    ? tramp_LubWorldCallbacks3d_friction
                    : NULL;
  o->restitution = lgen_callbacks_field(L, cb, 3, idx, "restitution")
                       ? tramp_LubWorldCallbacks3d_restitution
                       : NULL;
}

static void read_LubWorldOpts3d(lua_State *L, int idx, void *out_) {
  LubWorldOpts3d *o = (LubWorldOpts3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_version = lgen_int_opt(L, idx, "version", &o->version);
  if (lgen_has(L, idx, "gravity")) {
    lua_getfield(L, idx, "gravity");
    read_LubVec3d(L, -1, &o->gravity);
    lua_pop(L, 1);
    o->has_gravity = true;
  }
  o->has_fixed_dt = lgen_num_opt(L, idx, "fixed_dt", &o->fixed_dt);
  o->has_substeps = lgen_int_opt(L, idx, "substeps", &o->substeps);
  o->has_max_steps = lgen_int_opt(L, idx, "max_steps", &o->max_steps);
  o->has_sleep = lgen_bool_opt(L, idx, "sleep", &o->sleep);
  o->has_continuous = lgen_bool_opt(L, idx, "continuous", &o->continuous);
  o->has_hit_event_threshold =
      lgen_num_opt(L, idx, "hit_event_threshold", &o->hit_event_threshold);
  if (lgen_has(L, idx, "callbacks")) {
    lua_getfield(L, idx, "callbacks");
    read_LubWorldCallbacks3d(L, -1, &o->callbacks);
    lua_pop(L, 1);
    o->has_callbacks = true;
  }
}

static void read_LubBeginOpts3d(lua_State *L, int idx, void *out_) {
  LubBeginOpts3d *o = (LubBeginOpts3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_prune = lgen_bool_opt(L, idx, "prune", &o->prune);
}

static void read_LubBodyDesc3d(lua_State *L, int idx, void *out_) {
  LubBodyDesc3d *o = (LubBodyDesc3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_version = lgen_int_opt(L, idx, "version", &o->version);
  o->has_type = lgen_int_opt(L, idx, "type", &o->type);
  if (lgen_has(L, idx, "motion_locks")) {
    lua_getfield(L, idx, "motion_locks");
    read_LubMotionLocks3d(L, -1, &o->motion_locks);
    lua_pop(L, 1);
    o->has_motion_locks = true;
  }
  o->has_bullet = lgen_bool_opt(L, idx, "bullet", &o->bullet);
  o->has_enabled = lgen_bool_opt(L, idx, "enabled", &o->enabled);
  o->has_awake = lgen_bool_opt(L, idx, "awake", &o->awake);
  o->has_sleep = lgen_bool_opt(L, idx, "sleep", &o->sleep);
  o->has_sleep_threshold =
      lgen_num_opt(L, idx, "sleep_threshold", &o->sleep_threshold);
  o->has_gravity_scale =
      lgen_num_opt(L, idx, "gravity_scale", &o->gravity_scale);
  o->has_linear_damping =
      lgen_num_opt(L, idx, "linear_damping", &o->linear_damping);
  o->has_angular_damping =
      lgen_num_opt(L, idx, "angular_damping", &o->angular_damping);
  if (lgen_has(L, idx, "initial")) {
    lua_getfield(L, idx, "initial");
    read_LubInitialState3d(L, -1, &o->initial);
    lua_pop(L, 1);
    o->has_initial = true;
  }
}

static void read_LubFilterDesc3d(lua_State *L, int idx, void *out_) {
  LubFilterDesc3d *o = (LubFilterDesc3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_category_bits =
      lgen_bits_opt(L, idx, "category_bits", &o->category_bits);
  o->has_mask_bits = lgen_bits_opt(L, idx, "mask_bits", &o->mask_bits);
  o->has_group = lgen_int_opt(L, idx, "group", &o->group);
}

static void read_LubShapeDesc3d(lua_State *L, int idx, void *out_) {
  LubShapeDesc3d *o = (LubShapeDesc3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_version = lgen_int_opt(L, idx, "version", &o->version);
  o->has_density = lgen_num_opt(L, idx, "density", &o->density);
  o->has_friction = lgen_num_opt(L, idx, "friction", &o->friction);
  o->has_restitution = lgen_num_opt(L, idx, "restitution", &o->restitution);
  o->tag = lgen_str(L, idx, "tag");
  o->material_name = lgen_str(L, idx, "material_name");
  o->has_material_id = lgen_int_opt(L, idx, "material_id", &o->material_id);
  o->has_sensor = lgen_bool_opt(L, idx, "sensor", &o->sensor);
  o->has_contact = lgen_bool_opt(L, idx, "contact", &o->contact);
  o->has_hit = lgen_bool_opt(L, idx, "hit", &o->hit);
  o->has_sensor_events =
      lgen_bool_opt(L, idx, "sensor_events", &o->sensor_events);
  o->has_pre_solve = lgen_bool_opt(L, idx, "pre_solve", &o->pre_solve);
  if (lgen_has(L, idx, "filter")) {
    lua_getfield(L, idx, "filter");
    read_LubFilterDesc3d(L, -1, &o->filter);
    lua_pop(L, 1);
    o->has_filter = true;
  }
}

static void read_LubSphereDesc3d(lua_State *L, int idx, void *out_) {
  LubSphereDesc3d *o = (LubSphereDesc3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  read_LubShapeDesc3d(L, idx, &o->base);
  o->r = lgen_num(L, idx, "r", 0.0f);
  if (lgen_has(L, idx, "offset")) {
    lua_getfield(L, idx, "offset");
    read_LubVec3d(L, -1, &o->offset);
    lua_pop(L, 1);
    o->has_offset = true;
  }
}

static void read_LubBoxDesc3d(lua_State *L, int idx, void *out_) {
  LubBoxDesc3d *o = (LubBoxDesc3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  read_LubShapeDesc3d(L, idx, &o->base);
  o->hx = lgen_num(L, idx, "hx", 0.0f);
  o->hy = lgen_num(L, idx, "hy", 0.0f);
  o->hz = lgen_num(L, idx, "hz", 0.0f);
  if (lgen_has(L, idx, "offset")) {
    lua_getfield(L, idx, "offset");
    read_LubVec3d(L, -1, &o->offset);
    lua_pop(L, 1);
    o->has_offset = true;
  }
  if (lgen_has(L, idx, "quat")) {
    lua_getfield(L, idx, "quat");
    read_LubQuat3d(L, -1, &o->quat);
    lua_pop(L, 1);
    o->has_quat = true;
  }
}

static void read_LubCapsuleDesc3d(lua_State *L, int idx, void *out_) {
  LubCapsuleDesc3d *o = (LubCapsuleDesc3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  read_LubShapeDesc3d(L, idx, &o->base);
  if (lgen_has(L, idx, "a")) {
    lua_getfield(L, idx, "a");
    read_LubVec3d(L, -1, &o->a);
    lua_pop(L, 1);
  }
  if (lgen_has(L, idx, "b")) {
    lua_getfield(L, idx, "b");
    read_LubVec3d(L, -1, &o->b);
    lua_pop(L, 1);
  }
  o->r = lgen_num(L, idx, "r", 0.0f);
}

static void read_LubCylinderDesc3d(lua_State *L, int idx, void *out_) {
  LubCylinderDesc3d *o = (LubCylinderDesc3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  read_LubShapeDesc3d(L, idx, &o->base);
  o->height = lgen_num(L, idx, "height", 0.0f);
  o->radius = lgen_num(L, idx, "radius", 0.0f);
  o->has_sides = lgen_int_opt(L, idx, "sides", &o->sides);
  o->has_y_offset = lgen_num_opt(L, idx, "y_offset", &o->y_offset);
}

static void read_LubConeDesc3d(lua_State *L, int idx, void *out_) {
  LubConeDesc3d *o = (LubConeDesc3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  read_LubShapeDesc3d(L, idx, &o->base);
  o->height = lgen_num(L, idx, "height", 0.0f);
  o->radius1 = lgen_num(L, idx, "radius1", 0.0f);
  o->has_radius2 = lgen_num_opt(L, idx, "radius2", &o->radius2);
  o->has_slices = lgen_int_opt(L, idx, "slices", &o->slices);
}

static void read_LubHullDesc3d(lua_State *L, int idx, void *out_) {
  LubHullDesc3d *o = (LubHullDesc3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  read_LubShapeDesc3d(L, idx, &o->base);
  o->points = lgen_floats(L, idx, "points", &o->points_count);
  o->has_max_vertices = lgen_int_opt(L, idx, "max_vertices", &o->max_vertices);
}

static void read_LubSurfaceMaterial3d(lua_State *L, int idx, void *out_) {
  LubSurfaceMaterial3d *o = (LubSurfaceMaterial3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_friction = lgen_num_opt(L, idx, "friction", &o->friction);
  o->has_restitution = lgen_num_opt(L, idx, "restitution", &o->restitution);
  o->has_material_id = lgen_int_opt(L, idx, "material_id", &o->material_id);
}

static void read_LubMeshDesc3d(lua_State *L, int idx, void *out_) {
  LubMeshDesc3d *o = (LubMeshDesc3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  read_LubShapeDesc3d(L, idx, &o->base);
  o->positions = lgen_floats(L, idx, "positions", &o->positions_count);
  o->indices = lgen_ints(L, idx, "indices", &o->indices_count);
  if (lgen_has(L, idx, "scale")) {
    lua_getfield(L, idx, "scale");
    read_LubVec3d(L, -1, &o->scale);
    lua_pop(L, 1);
    o->has_scale = true;
  }
  o->has_weld_vertices =
      lgen_bool_opt(L, idx, "weld_vertices", &o->weld_vertices);
  o->has_weld_tolerance =
      lgen_num_opt(L, idx, "weld_tolerance", &o->weld_tolerance);
  o->has_use_median_split =
      lgen_bool_opt(L, idx, "use_median_split", &o->use_median_split);
  o->has_identify_edges =
      lgen_bool_opt(L, idx, "identify_edges", &o->identify_edges);
  o->materials = (const LubSurfaceMaterial3d *)lgen_records(
      L, idx, "materials", sizeof(LubSurfaceMaterial3d),
      read_LubSurfaceMaterial3d, &o->materials_count);
  o->material_indices =
      lgen_ints(L, idx, "material_indices", &o->material_indices_count);
}

static void read_LubHeightFieldDesc3d(lua_State *L, int idx, void *out_) {
  LubHeightFieldDesc3d *o = (LubHeightFieldDesc3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  read_LubShapeDesc3d(L, idx, &o->base);
  o->heights = lgen_floats(L, idx, "heights", &o->heights_count);
  o->x_count = lgen_int(L, idx, "x_count", 0);
  o->z_count = lgen_int(L, idx, "z_count", 0);
  o->has_cell_width = lgen_num_opt(L, idx, "cell_width", &o->cell_width);
  if (lgen_has(L, idx, "scale")) {
    lua_getfield(L, idx, "scale");
    read_LubVec3d(L, -1, &o->scale);
    lua_pop(L, 1);
    o->has_scale = true;
  }
  o->has_min_height = lgen_num_opt(L, idx, "min_height", &o->min_height);
  o->has_max_height = lgen_num_opt(L, idx, "max_height", &o->max_height);
  o->has_clockwise_winding =
      lgen_bool_opt(L, idx, "clockwise_winding", &o->clockwise_winding);
}

static void read_LubCompoundSphere3d(lua_State *L, int idx, void *out_) {
  LubCompoundSphere3d *o = (LubCompoundSphere3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->r = lgen_num(L, idx, "r", 0.0f);
  if (lgen_has(L, idx, "center")) {
    lua_getfield(L, idx, "center");
    read_LubVec3d(L, -1, &o->center);
    lua_pop(L, 1);
    o->has_center = true;
  }
}

static void read_LubCompoundBox3d(lua_State *L, int idx, void *out_) {
  LubCompoundBox3d *o = (LubCompoundBox3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->hx = lgen_num(L, idx, "hx", 0.0f);
  o->hy = lgen_num(L, idx, "hy", 0.0f);
  o->hz = lgen_num(L, idx, "hz", 0.0f);
}

static void read_LubCompoundCapsule3d(lua_State *L, int idx, void *out_) {
  LubCompoundCapsule3d *o = (LubCompoundCapsule3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  if (lgen_has(L, idx, "a")) {
    lua_getfield(L, idx, "a");
    read_LubVec3d(L, -1, &o->a);
    lua_pop(L, 1);
  }
  if (lgen_has(L, idx, "b")) {
    lua_getfield(L, idx, "b");
    read_LubVec3d(L, -1, &o->b);
    lua_pop(L, 1);
  }
  o->r = lgen_num(L, idx, "r", 0.0f);
}

static void read_LubCompoundChild3d(lua_State *L, int idx, void *out_) {
  LubCompoundChild3d *o = (LubCompoundChild3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  if (lgen_has(L, idx, "pose")) {
    lua_getfield(L, idx, "pose");
    read_LubFrameDesc3d(L, -1, &o->pose);
    lua_pop(L, 1);
    o->has_pose = true;
  }
  o->has_friction = lgen_num_opt(L, idx, "friction", &o->friction);
  o->has_restitution = lgen_num_opt(L, idx, "restitution", &o->restitution);
  o->has_material_id = lgen_int_opt(L, idx, "material_id", &o->material_id);
  if (lgen_has(L, idx, "sphere")) {
    lua_getfield(L, idx, "sphere");
    read_LubCompoundSphere3d(L, -1, &o->sphere);
    lua_pop(L, 1);
    o->has_sphere = true;
  }
  if (lgen_has(L, idx, "box")) {
    lua_getfield(L, idx, "box");
    read_LubCompoundBox3d(L, -1, &o->box);
    lua_pop(L, 1);
    o->has_box = true;
  }
  if (lgen_has(L, idx, "capsule")) {
    lua_getfield(L, idx, "capsule");
    read_LubCompoundCapsule3d(L, -1, &o->capsule);
    lua_pop(L, 1);
    o->has_capsule = true;
  }
}

static void read_LubCompoundDesc3d(lua_State *L, int idx, void *out_) {
  LubCompoundDesc3d *o = (LubCompoundDesc3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  read_LubShapeDesc3d(L, idx, &o->base);
  o->children = (const LubCompoundChild3d *)lgen_records(
      L, idx, "children", sizeof(LubCompoundChild3d), read_LubCompoundChild3d,
      &o->children_count);
}

static void read_LubCommandOpts3d(lua_State *L, int idx, void *out_) {
  LubCommandOpts3d *o = (LubCommandOpts3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_wake = lgen_bool_opt(L, idx, "wake", &o->wake);
  if (lgen_has(L, idx, "point")) {
    lua_getfield(L, idx, "point");
    read_LubVec3d(L, -1, &o->point);
    lua_pop(L, 1);
    o->has_point = true;
  }
}

static void read_LubVelocityDesc3d(lua_State *L, int idx, void *out_) {
  LubVelocityDesc3d *o = (LubVelocityDesc3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_vx = lgen_num_opt(L, idx, "vx", &o->vx);
  o->has_vy = lgen_num_opt(L, idx, "vy", &o->vy);
  o->has_vz = lgen_num_opt(L, idx, "vz", &o->vz);
  o->has_wx = lgen_num_opt(L, idx, "wx", &o->wx);
  o->has_wy = lgen_num_opt(L, idx, "wy", &o->wy);
  o->has_wz = lgen_num_opt(L, idx, "wz", &o->wz);
}

static void read_LubPoseDesc3d(lua_State *L, int idx, void *out_) {
  LubPoseDesc3d *o = (LubPoseDesc3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_x = lgen_num_opt(L, idx, "x", &o->x);
  o->has_y = lgen_num_opt(L, idx, "y", &o->y);
  o->has_z = lgen_num_opt(L, idx, "z", &o->z);
  if (lgen_has(L, idx, "quat")) {
    lua_getfield(L, idx, "quat");
    read_LubQuat3d(L, -1, &o->quat);
    lua_pop(L, 1);
    o->has_quat = true;
  }
  if (lgen_has(L, idx, "euler")) {
    lua_getfield(L, idx, "euler");
    read_LubVec3d(L, -1, &o->euler);
    lua_pop(L, 1);
    o->has_euler = true;
  }
}

static void read_LubTargetDesc3d(lua_State *L, int idx, void *out_) {
  LubTargetDesc3d *o = (LubTargetDesc3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_x = lgen_num_opt(L, idx, "x", &o->x);
  o->has_y = lgen_num_opt(L, idx, "y", &o->y);
  o->has_z = lgen_num_opt(L, idx, "z", &o->z);
  if (lgen_has(L, idx, "quat")) {
    lua_getfield(L, idx, "quat");
    read_LubQuat3d(L, -1, &o->quat);
    lua_pop(L, 1);
    o->has_quat = true;
  }
  if (lgen_has(L, idx, "euler")) {
    lua_getfield(L, idx, "euler");
    read_LubVec3d(L, -1, &o->euler);
    lua_pop(L, 1);
    o->has_euler = true;
  }
  o->has_time_step = lgen_num_opt(L, idx, "time_step", &o->time_step);
  o->has_wake = lgen_bool_opt(L, idx, "wake", &o->wake);
}

static void read_LubFrameDesc3d(lua_State *L, int idx, void *out_) {
  LubFrameDesc3d *o = (LubFrameDesc3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_x = lgen_num_opt(L, idx, "x", &o->x);
  o->has_y = lgen_num_opt(L, idx, "y", &o->y);
  o->has_z = lgen_num_opt(L, idx, "z", &o->z);
  if (lgen_has(L, idx, "quat")) {
    lua_getfield(L, idx, "quat");
    read_LubQuat3d(L, -1, &o->quat);
    lua_pop(L, 1);
    o->has_quat = true;
  }
  if (lgen_has(L, idx, "euler")) {
    lua_getfield(L, idx, "euler");
    read_LubVec3d(L, -1, &o->euler);
    lua_pop(L, 1);
    o->has_euler = true;
  }
}

static void read_LubJointSpringDesc3d(lua_State *L, int idx, void *out_) {
  LubJointSpringDesc3d *o = (LubJointSpringDesc3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_enabled = lgen_bool_opt(L, idx, "enabled", &o->enabled);
  o->has_hertz = lgen_num_opt(L, idx, "hertz", &o->hertz);
  o->has_damping_ratio =
      lgen_num_opt(L, idx, "damping_ratio", &o->damping_ratio);
  o->has_linear_hertz = lgen_num_opt(L, idx, "linear_hertz", &o->linear_hertz);
  o->has_linear_damping_ratio =
      lgen_num_opt(L, idx, "linear_damping_ratio", &o->linear_damping_ratio);
  o->has_angular_hertz =
      lgen_num_opt(L, idx, "angular_hertz", &o->angular_hertz);
  o->has_angular_damping_ratio =
      lgen_num_opt(L, idx, "angular_damping_ratio", &o->angular_damping_ratio);
  o->has_max_torque = lgen_num_opt(L, idx, "max_torque", &o->max_torque);
}

static void read_LubJointLimitDesc3d(lua_State *L, int idx, void *out_) {
  LubJointLimitDesc3d *o = (LubJointLimitDesc3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_enabled = lgen_bool_opt(L, idx, "enabled", &o->enabled);
  o->has_lower = lgen_num_opt(L, idx, "lower", &o->lower);
  o->has_upper = lgen_num_opt(L, idx, "upper", &o->upper);
  o->has_min_length = lgen_num_opt(L, idx, "min_length", &o->min_length);
  o->has_max_length = lgen_num_opt(L, idx, "max_length", &o->max_length);
  o->has_cone_angle = lgen_num_opt(L, idx, "cone_angle", &o->cone_angle);
  o->has_lower_twist_angle =
      lgen_num_opt(L, idx, "lower_twist_angle", &o->lower_twist_angle);
  o->has_upper_twist_angle =
      lgen_num_opt(L, idx, "upper_twist_angle", &o->upper_twist_angle);
}

static void read_LubJointMotorDesc3d(lua_State *L, int idx, void *out_) {
  LubJointMotorDesc3d *o = (LubJointMotorDesc3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_enabled = lgen_bool_opt(L, idx, "enabled", &o->enabled);
  o->has_speed = lgen_num_opt(L, idx, "speed", &o->speed);
  o->has_max_force = lgen_num_opt(L, idx, "max_force", &o->max_force);
  o->has_max_torque = lgen_num_opt(L, idx, "max_torque", &o->max_torque);
  if (lgen_has(L, idx, "velocity")) {
    lua_getfield(L, idx, "velocity");
    read_LubVec3d(L, -1, &o->velocity);
    lua_pop(L, 1);
    o->has_velocity = true;
  }
  if (lgen_has(L, idx, "linear_velocity")) {
    lua_getfield(L, idx, "linear_velocity");
    read_LubVec3d(L, -1, &o->linear_velocity);
    lua_pop(L, 1);
    o->has_linear_velocity = true;
  }
  if (lgen_has(L, idx, "angular_velocity")) {
    lua_getfield(L, idx, "angular_velocity");
    read_LubVec3d(L, -1, &o->angular_velocity);
    lua_pop(L, 1);
    o->has_angular_velocity = true;
  }
  o->has_max_velocity_force =
      lgen_num_opt(L, idx, "max_velocity_force", &o->max_velocity_force);
  o->has_max_velocity_torque =
      lgen_num_opt(L, idx, "max_velocity_torque", &o->max_velocity_torque);
}

static void read_LubJointTargetDesc3d(lua_State *L, int idx, void *out_) {
  LubJointTargetDesc3d *o = (LubJointTargetDesc3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_translation = lgen_num_opt(L, idx, "translation", &o->translation);
  o->has_angle = lgen_num_opt(L, idx, "angle", &o->angle);
  o->has_steering_angle =
      lgen_num_opt(L, idx, "steering_angle", &o->steering_angle);
  if (lgen_has(L, idx, "quat")) {
    lua_getfield(L, idx, "quat");
    read_LubQuat3d(L, -1, &o->quat);
    lua_pop(L, 1);
    o->has_quat = true;
  }
  if (lgen_has(L, idx, "euler")) {
    lua_getfield(L, idx, "euler");
    read_LubVec3d(L, -1, &o->euler);
    lua_pop(L, 1);
    o->has_euler = true;
  }
  if (lgen_has(L, idx, "linear_velocity")) {
    lua_getfield(L, idx, "linear_velocity");
    read_LubVec3d(L, -1, &o->linear_velocity);
    lua_pop(L, 1);
    o->has_linear_velocity = true;
  }
  if (lgen_has(L, idx, "angular_velocity")) {
    lua_getfield(L, idx, "angular_velocity");
    read_LubVec3d(L, -1, &o->angular_velocity);
    lua_pop(L, 1);
    o->has_angular_velocity = true;
  }
}

static void read_LubJointDesc3d(lua_State *L, int idx, void *out_) {
  LubJointDesc3d *o = (LubJointDesc3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_version = lgen_int_opt(L, idx, "version", &o->version);
  o->type = lgen_enum_str(L, idx, "type", names_LubPhys3dJointType,
                          values_LubPhys3dJointType, "JointType", &o->has_type);
  o->body_a = lgen_ref(L, idx, "body_a", "body3d");
  o->body_b = lgen_ref(L, idx, "body_b", "body3d");
  if (lgen_has(L, idx, "anchor_a")) {
    lua_getfield(L, idx, "anchor_a");
    read_LubVec3d(L, -1, &o->anchor_a);
    lua_pop(L, 1);
    o->has_anchor_a = true;
  }
  if (lgen_has(L, idx, "anchor_b")) {
    lua_getfield(L, idx, "anchor_b");
    read_LubVec3d(L, -1, &o->anchor_b);
    lua_pop(L, 1);
    o->has_anchor_b = true;
  }
  if (lgen_has(L, idx, "axis")) {
    lua_getfield(L, idx, "axis");
    read_LubVec3d(L, -1, &o->axis);
    lua_pop(L, 1);
    o->has_axis = true;
  }
  if (lgen_has(L, idx, "frame_a")) {
    lua_getfield(L, idx, "frame_a");
    read_LubFrameDesc3d(L, -1, &o->frame_a);
    lua_pop(L, 1);
    o->has_frame_a = true;
  }
  if (lgen_has(L, idx, "frame_b")) {
    lua_getfield(L, idx, "frame_b");
    read_LubFrameDesc3d(L, -1, &o->frame_b);
    lua_pop(L, 1);
    o->has_frame_b = true;
  }
  o->has_collide_connected =
      lgen_bool_opt(L, idx, "collide_connected", &o->collide_connected);
  o->has_force_threshold =
      lgen_num_opt(L, idx, "force_threshold", &o->force_threshold);
  o->has_torque_threshold =
      lgen_num_opt(L, idx, "torque_threshold", &o->torque_threshold);
  o->has_constraint_hertz =
      lgen_num_opt(L, idx, "constraint_hertz", &o->constraint_hertz);
  o->has_constraint_damping_ratio = lgen_num_opt(
      L, idx, "constraint_damping_ratio", &o->constraint_damping_ratio);
  o->has_length = lgen_num_opt(L, idx, "length", &o->length);
  o->has_min_length = lgen_num_opt(L, idx, "min_length", &o->min_length);
  o->has_max_length = lgen_num_opt(L, idx, "max_length", &o->max_length);
  o->has_lower = lgen_num_opt(L, idx, "lower", &o->lower);
  o->has_upper = lgen_num_opt(L, idx, "upper", &o->upper);
  o->has_hertz = lgen_num_opt(L, idx, "hertz", &o->hertz);
  o->has_damping_ratio =
      lgen_num_opt(L, idx, "damping_ratio", &o->damping_ratio);
  o->has_linear_hertz = lgen_num_opt(L, idx, "linear_hertz", &o->linear_hertz);
  o->has_angular_hertz =
      lgen_num_opt(L, idx, "angular_hertz", &o->angular_hertz);
  o->has_linear_damping_ratio =
      lgen_num_opt(L, idx, "linear_damping_ratio", &o->linear_damping_ratio);
  o->has_angular_damping_ratio =
      lgen_num_opt(L, idx, "angular_damping_ratio", &o->angular_damping_ratio);
  o->has_max_force = lgen_num_opt(L, idx, "max_force", &o->max_force);
  o->has_max_torque = lgen_num_opt(L, idx, "max_torque", &o->max_torque);
  o->has_max_velocity_force =
      lgen_num_opt(L, idx, "max_velocity_force", &o->max_velocity_force);
  o->has_max_velocity_torque =
      lgen_num_opt(L, idx, "max_velocity_torque", &o->max_velocity_torque);
  o->has_max_spring_force =
      lgen_num_opt(L, idx, "max_spring_force", &o->max_spring_force);
  o->has_max_spring_torque =
      lgen_num_opt(L, idx, "max_spring_torque", &o->max_spring_torque);
  o->has_motor_speed = lgen_num_opt(L, idx, "motor_speed", &o->motor_speed);
  o->has_target_angle = lgen_num_opt(L, idx, "target_angle", &o->target_angle);
  o->has_target_translation =
      lgen_num_opt(L, idx, "target_translation", &o->target_translation);
  if (lgen_has(L, idx, "target_rotation")) {
    lua_getfield(L, idx, "target_rotation");
    read_LubQuat3d(L, -1, &o->target_rotation);
    lua_pop(L, 1);
    o->has_target_rotation = true;
  }
  if (lgen_has(L, idx, "linear_velocity")) {
    lua_getfield(L, idx, "linear_velocity");
    read_LubVec3d(L, -1, &o->linear_velocity);
    lua_pop(L, 1);
    o->has_linear_velocity = true;
  }
  if (lgen_has(L, idx, "angular_velocity")) {
    lua_getfield(L, idx, "angular_velocity");
    read_LubVec3d(L, -1, &o->angular_velocity);
    lua_pop(L, 1);
    o->has_angular_velocity = true;
  }
  if (lgen_has(L, idx, "motor_velocity")) {
    lua_getfield(L, idx, "motor_velocity");
    read_LubVec3d(L, -1, &o->motor_velocity);
    lua_pop(L, 1);
    o->has_motor_velocity = true;
  }
  o->has_enable_spring =
      lgen_bool_opt(L, idx, "enable_spring", &o->enable_spring);
  o->has_enable_limit = lgen_bool_opt(L, idx, "enable_limit", &o->enable_limit);
  o->has_enable_motor = lgen_bool_opt(L, idx, "enable_motor", &o->enable_motor);
  o->has_cone_angle = lgen_num_opt(L, idx, "cone_angle", &o->cone_angle);
  o->has_enable_cone_limit =
      lgen_bool_opt(L, idx, "enable_cone_limit", &o->enable_cone_limit);
  o->has_enable_twist_limit =
      lgen_bool_opt(L, idx, "enable_twist_limit", &o->enable_twist_limit);
  o->has_lower_twist_angle =
      lgen_num_opt(L, idx, "lower_twist_angle", &o->lower_twist_angle);
  o->has_upper_twist_angle =
      lgen_num_opt(L, idx, "upper_twist_angle", &o->upper_twist_angle);
  if (lgen_has(L, idx, "spring")) {
    lua_getfield(L, idx, "spring");
    read_LubJointSpringDesc3d(L, -1, &o->spring);
    lua_pop(L, 1);
    o->has_spring = true;
  }
  if (lgen_has(L, idx, "limit")) {
    lua_getfield(L, idx, "limit");
    read_LubJointLimitDesc3d(L, -1, &o->limit);
    lua_pop(L, 1);
    o->has_limit = true;
  }
  if (lgen_has(L, idx, "motor")) {
    lua_getfield(L, idx, "motor");
    read_LubJointMotorDesc3d(L, -1, &o->motor);
    lua_pop(L, 1);
    o->has_motor = true;
  }
}

static void read_LubMaterialDesc3d(lua_State *L, int idx, void *out_) {
  LubMaterialDesc3d *o = (LubMaterialDesc3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_density = lgen_num_opt(L, idx, "density", &o->density);
  o->has_friction = lgen_num_opt(L, idx, "friction", &o->friction);
  o->has_restitution = lgen_num_opt(L, idx, "restitution", &o->restitution);
  o->material_name = lgen_str(L, idx, "material_name");
  o->has_material_id = lgen_int_opt(L, idx, "material_id", &o->material_id);
}

static void read_LubShapeEventsDesc3d(lua_State *L, int idx, void *out_) {
  LubShapeEventsDesc3d *o = (LubShapeEventsDesc3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_sensor_events =
      lgen_bool_opt(L, idx, "sensor_events", &o->sensor_events);
  o->has_contact = lgen_bool_opt(L, idx, "contact", &o->contact);
  o->has_pre_solve = lgen_bool_opt(L, idx, "pre_solve", &o->pre_solve);
  o->has_hit = lgen_bool_opt(L, idx, "hit", &o->hit);
}

static void read_LubMoverDesc3d(lua_State *L, int idx, void *out_) {
  LubMoverDesc3d *o = (LubMoverDesc3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  if (lgen_has(L, idx, "a")) {
    lua_getfield(L, idx, "a");
    read_LubVec3d(L, -1, &o->a);
    lua_pop(L, 1);
  }
  if (lgen_has(L, idx, "b")) {
    lua_getfield(L, idx, "b");
    read_LubVec3d(L, -1, &o->b);
    lua_pop(L, 1);
  }
  o->r = lgen_num(L, idx, "r", 0.0f);
  o->has_dx = lgen_num_opt(L, idx, "dx", &o->dx);
  o->has_dy = lgen_num_opt(L, idx, "dy", &o->dy);
  o->has_dz = lgen_num_opt(L, idx, "dz", &o->dz);
  o->has_max_fraction = lgen_num_opt(L, idx, "max_fraction", &o->max_fraction);
  if (lgen_has(L, idx, "filter")) {
    lua_getfield(L, idx, "filter");
    read_LubFilterDesc3d(L, -1, &o->filter);
    lua_pop(L, 1);
    o->has_filter = true;
  }
}

static void read_LubRaycastDesc3d(lua_State *L, int idx, void *out_) {
  LubRaycastDesc3d *o = (LubRaycastDesc3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->has_x = lgen_num_opt(L, idx, "x", &o->x);
  o->has_y = lgen_num_opt(L, idx, "y", &o->y);
  o->has_z = lgen_num_opt(L, idx, "z", &o->z);
  o->has_dx = lgen_num_opt(L, idx, "dx", &o->dx);
  o->has_dy = lgen_num_opt(L, idx, "dy", &o->dy);
  o->has_dz = lgen_num_opt(L, idx, "dz", &o->dz);
  o->has_max_fraction = lgen_num_opt(L, idx, "max_fraction", &o->max_fraction);
  if (lgen_has(L, idx, "filter")) {
    lua_getfield(L, idx, "filter");
    read_LubFilterDesc3d(L, -1, &o->filter);
    lua_pop(L, 1);
    o->has_filter = true;
  }
}

static void read_LubAabbDesc3d(lua_State *L, int idx, void *out_) {
  LubAabbDesc3d *o = (LubAabbDesc3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->min_x = lgen_num(L, idx, "min_x", 0.0f);
  o->min_y = lgen_num(L, idx, "min_y", 0.0f);
  o->min_z = lgen_num(L, idx, "min_z", 0.0f);
  o->max_x = lgen_num(L, idx, "max_x", 0.0f);
  o->max_y = lgen_num(L, idx, "max_y", 0.0f);
  o->max_z = lgen_num(L, idx, "max_z", 0.0f);
  if (lgen_has(L, idx, "filter")) {
    lua_getfield(L, idx, "filter");
    read_LubFilterDesc3d(L, -1, &o->filter);
    lua_pop(L, 1);
    o->has_filter = true;
  }
}

static void read_LubSphereProxy3d(lua_State *L, int idx, void *out_) {
  LubSphereProxy3d *o = (LubSphereProxy3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->r = lgen_num(L, idx, "r", 0.0f);
  if (lgen_has(L, idx, "center")) {
    lua_getfield(L, idx, "center");
    read_LubVec3d(L, -1, &o->center);
    lua_pop(L, 1);
    o->has_center = true;
  }
}

static void read_LubBoxProxy3d(lua_State *L, int idx, void *out_) {
  LubBoxProxy3d *o = (LubBoxProxy3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  o->hx = lgen_num(L, idx, "hx", 0.0f);
  o->hy = lgen_num(L, idx, "hy", 0.0f);
  o->hz = lgen_num(L, idx, "hz", 0.0f);
  o->has_radius = lgen_num_opt(L, idx, "radius", &o->radius);
  if (lgen_has(L, idx, "center")) {
    lua_getfield(L, idx, "center");
    read_LubVec3d(L, -1, &o->center);
    lua_pop(L, 1);
    o->has_center = true;
  }
  if (lgen_has(L, idx, "quat")) {
    lua_getfield(L, idx, "quat");
    read_LubQuat3d(L, -1, &o->quat);
    lua_pop(L, 1);
    o->has_quat = true;
  }
}

static void read_LubCapsuleProxy3d(lua_State *L, int idx, void *out_) {
  LubCapsuleProxy3d *o = (LubCapsuleProxy3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  if (lgen_has(L, idx, "a")) {
    lua_getfield(L, idx, "a");
    read_LubVec3d(L, -1, &o->a);
    lua_pop(L, 1);
  }
  if (lgen_has(L, idx, "b")) {
    lua_getfield(L, idx, "b");
    read_LubVec3d(L, -1, &o->b);
    lua_pop(L, 1);
  }
  o->r = lgen_num(L, idx, "r", 0.0f);
}

static void read_LubShapeProxyDesc3d(lua_State *L, int idx, void *out_) {
  LubShapeProxyDesc3d *o = (LubShapeProxyDesc3d *)out_;
  idx = lua_absindex(L, idx);
  (void)o;
  if (lgen_has(L, idx, "sphere")) {
    lua_getfield(L, idx, "sphere");
    read_LubSphereProxy3d(L, -1, &o->sphere);
    lua_pop(L, 1);
    o->has_sphere = true;
  }
  if (lgen_has(L, idx, "box")) {
    lua_getfield(L, idx, "box");
    read_LubBoxProxy3d(L, -1, &o->box);
    lua_pop(L, 1);
    o->has_box = true;
  }
  if (lgen_has(L, idx, "capsule")) {
    lua_getfield(L, idx, "capsule");
    read_LubCapsuleProxy3d(L, -1, &o->capsule);
    lua_pop(L, 1);
    o->has_capsule = true;
  }
  o->has_dx = lgen_num_opt(L, idx, "dx", &o->dx);
  o->has_dy = lgen_num_opt(L, idx, "dy", &o->dy);
  o->has_dz = lgen_num_opt(L, idx, "dz", &o->dz);
  o->has_max_fraction = lgen_num_opt(L, idx, "max_fraction", &o->max_fraction);
  if (lgen_has(L, idx, "filter")) {
    lua_getfield(L, idx, "filter");
    read_LubFilterDesc3d(L, -1, &o->filter);
    lua_pop(L, 1);
    o->has_filter = true;
  }
}

static void fill_LubMeshData(lua_State *L, const LubMeshData *v) {
  if (v->positions) {
    lgen_push_float_view(L, v->positions, v->positions_count);
    lua_setfield(L, -2, "positions");
  }
  if (v->normals) {
    lgen_push_float_view(L, v->normals, v->normals_count);
    lua_setfield(L, -2, "normals");
  }
  if (v->indices) {
    lgen_push_int_view(L, v->indices, v->indices_count);
    lua_setfield(L, -2, "indices");
  }
  lgen_set_int(L, "vert_count", v->vert_count);
  lgen_set_int(L, "index_count", v->index_count);
  if (v->uvs) {
    lgen_push_float_view(L, v->uvs, v->uvs_count);
    lua_setfield(L, -2, "uvs");
  }
  if (v->tangents) {
    lgen_push_float_view(L, v->tangents, v->tangents_count);
    lua_setfield(L, -2, "tangents");
  }
  if (v->bounds_min) {
    lgen_push_float_view(L, v->bounds_min, v->bounds_min_count);
    lua_setfield(L, -2, "bounds_min");
  }
  if (v->bounds_max) {
    lgen_push_float_view(L, v->bounds_max, v->bounds_max_count);
    lua_setfield(L, -2, "bounds_max");
  }
  if (v->has_cell)
    lgen_set_num(L, "cell", v->cell);
  if (v->colors) {
    lgen_push_float_view(L, v->colors, v->colors_count);
    lua_setfield(L, -2, "colors");
  }
  if (v->metal_rough) {
    lgen_push_float_view(L, v->metal_rough, v->metal_rough_count);
    lua_setfield(L, -2, "metal_rough");
  }
  if (v->joints) {
    lgen_push_int_view(L, v->joints, v->joints_count);
    lua_setfield(L, -2, "joints");
  }
  if (v->weights) {
    lgen_push_float_view(L, v->weights, v->weights_count);
    lua_setfield(L, -2, "weights");
  }
  if (v->bones) {
    push_list_LubSdfBone(L, v->bones, v->bones_count);
    lua_setfield(L, -2, "bones");
  }
  (void)L;
  (void)v;
}

static void push_LubMeshData(lua_State *L, const LubMeshData *v) {
  lua_createtable(L, 0, 15);
  fill_LubMeshData(L, v);
}

static void fill_LubSdfBone(lua_State *L, const LubSdfBone *v) {
  lgen_set_str(L, "name", v->name);
  lgen_set_num(L, "x", v->x);
  lgen_set_num(L, "y", v->y);
  lgen_set_num(L, "z", v->z);
  (void)L;
  (void)v;
}

static void push_LubSdfBone(lua_State *L, const LubSdfBone *v) {
  lua_createtable(L, 0, 4);
  fill_LubSdfBone(L, v);
}

static void push_list_LubSdfBone(lua_State *L, const LubSdfBone *v, int32_t n) {
  lua_createtable(L, n, 0);
  for (int32_t i = 0; i < n; ++i) {
    push_LubSdfBone(L, &v[i]);
    lua_rawseti(L, -2, i + 1);
  }
}

static void fill_LubGltfMaterial(lua_State *L, const LubGltfMaterial *v) {
  {
    lgen_push_float_table(L, v->base_color_factor, v->base_color_factor_count);
    lua_setfield(L, -2, "base_color_factor");
  }
  lgen_set_num(L, "metallic_factor", v->metallic_factor);
  lgen_set_num(L, "roughness_factor", v->roughness_factor);
  lgen_set_int(L, "alpha_mode", v->alpha_mode);
  lgen_set_num(L, "alpha_cutoff", v->alpha_cutoff);
  lgen_set_bool(L, "double_sided", v->double_sided);
  lgen_set_num(L, "normal_scale", v->normal_scale);
  if (v->base_color_path.len > 0)
    lgen_set_str(L, "base_color_path", v->base_color_path);
  if (v->metallic_roughness_path.len > 0)
    lgen_set_str(L, "metallic_roughness_path", v->metallic_roughness_path);
  if (v->normal_path.len > 0)
    lgen_set_str(L, "normal_path", v->normal_path);
  if (v->name.len > 0)
    lgen_set_str(L, "name", v->name);
  (void)L;
  (void)v;
}

static void push_LubGltfMaterial(lua_State *L, const LubGltfMaterial *v) {
  lua_createtable(L, 0, 11);
  fill_LubGltfMaterial(L, v);
}

static void fill_LubGltfPrimitive(lua_State *L, const LubGltfPrimitive *v) {
  fill_LubMeshData(L, &v->base);
  lgen_set_int(L, "material_index", v->material_index);
  if (v->has_material) {
    push_LubGltfMaterial(L, &v->material);
    lua_setfield(L, -2, "material");
  }
  (void)L;
  (void)v;
}

static void push_LubGltfPrimitive(lua_State *L, const LubGltfPrimitive *v) {
  lua_createtable(L, 0, 17);
  fill_LubGltfPrimitive(L, v);
}

static void push_list_LubGltfPrimitive(lua_State *L, const LubGltfPrimitive *v,
                                       int32_t n) {
  lua_createtable(L, n, 0);
  for (int32_t i = 0; i < n; ++i) {
    push_LubGltfPrimitive(L, &v[i]);
    lua_rawseti(L, -2, i + 1);
  }
}

static void fill_LubGltfMesh(lua_State *L, const LubGltfMesh *v) {
  fill_LubMeshData(L, &v->base);
  if (v->primitives) {
    push_list_LubGltfPrimitive(L, v->primitives, v->primitives_count);
    lua_setfield(L, -2, "primitives");
  }
  if (v->has_material) {
    push_LubGltfMaterial(L, &v->material);
    lua_setfield(L, -2, "material");
  }
  (void)L;
  (void)v;
}

static void push_LubGltfMesh(lua_State *L, const LubGltfMesh *v) {
  lua_createtable(L, 0, 17);
  fill_LubGltfMesh(L, v);
}

static void fill_LubGlyphBitmap(lua_State *L, const LubGlyphBitmap *v) {
  lgen_set_int(L, "w", v->w);
  lgen_set_int(L, "h", v->h);
  lgen_set_int(L, "xoff", v->xoff);
  lgen_set_int(L, "yoff", v->yoff);
  lgen_set_num(L, "advance", v->advance);
  if (v->bytes.len > 0)
    lgen_set_str(L, "bytes", v->bytes);
  (void)L;
  (void)v;
}

static void push_LubGlyphBitmap(lua_State *L, const LubGlyphBitmap *v) {
  lua_createtable(L, 0, 6);
  fill_LubGlyphBitmap(L, v);
}

static void fill_LubGlyphMesh(lua_State *L, const LubGlyphMesh *v) {
  fill_LubMeshData(L, &v->base);
  lgen_set_num(L, "advance", v->advance);
  (void)L;
  (void)v;
}

static void push_LubGlyphMesh(lua_State *L, const LubGlyphMesh *v) {
  lua_createtable(L, 0, 16);
  fill_LubGlyphMesh(L, v);
}

static void fill_LubFontMetrics(lua_State *L, const LubFontMetrics *v) {
  lgen_set_num(L, "ascent", v->ascent);
  lgen_set_num(L, "descent", v->descent);
  lgen_set_num(L, "line_gap", v->line_gap);
  (void)L;
  (void)v;
}

static void push_LubFontMetrics(lua_State *L, const LubFontMetrics *v) {
  lua_createtable(L, 0, 3);
  fill_LubFontMetrics(L, v);
}

static void fill_LubAudioInfo(lua_State *L, const LubAudioInfo *v) {
  lgen_set_bool(L, "device", v->device);
  lgen_set_int(L, "rate", v->rate);
  lgen_set_int(L, "voices", v->voices);
  lgen_set_int(L, "snds", v->snds);
  (void)L;
  (void)v;
}

static void push_LubAudioInfo(lua_State *L, const LubAudioInfo *v) {
  lua_createtable(L, 0, 4);
  fill_LubAudioInfo(L, v);
}

static void fill_LubVec2d(lua_State *L, const LubVec2d *v) {
  lgen_set_num(L, "x", v->x);
  lgen_set_num(L, "y", v->y);
  (void)L;
  (void)v;
}

static void push_LubVec2d(lua_State *L, const LubVec2d *v) {
  lua_createtable(L, 0, 2);
  fill_LubVec2d(L, v);
}

static void fill_LubShapeView(lua_State *L, const LubShapeView *v) {
  lgen_set_str(L, "body", v->body);
  lgen_set_str(L, "shape", v->shape);
  if (v->tag.len > 0)
    lgen_set_str(L, "tag", v->tag);
  if (v->chain.len > 0)
    lgen_set_str(L, "chain", v->chain);
  if (v->has_segment)
    lgen_set_bool(L, "segment", v->segment);
  if (v->material_name.len > 0)
    lgen_set_str(L, "material_name", v->material_name);
  if (v->has_material_id)
    lgen_set_int(L, "material_id", v->material_id);
  if (v->has_kind && name_LubPhys2dShapeKind(v->kind)) {
    lua_pushstring(L, name_LubPhys2dShapeKind(v->kind));
    lua_setfield(L, -2, "kind");
  }
  if (v->has_category_bits)
    lgen_set_bits(L, "category_bits", v->category_bits);
  if (v->has_mask_bits)
    lgen_set_bits(L, "mask_bits", v->mask_bits);
  if (v->has_group)
    lgen_set_int(L, "group", v->group);
  lgen_set_bool(L, "valid", v->valid);
  (void)L;
  (void)v;
}

static void push_LubShapeView(lua_State *L, const LubShapeView *v) {
  lua_createtable(L, 0, 12);
  fill_LubShapeView(L, v);
}

static void push_list_LubShapeView(lua_State *L, const LubShapeView *v,
                                   int32_t n) {
  lua_createtable(L, n, 0);
  for (int32_t i = 0; i < n; ++i) {
    push_LubShapeView(L, &v[i]);
    lua_rawseti(L, -2, i + 1);
  }
}

static void fill_LubMaterialView(lua_State *L, const LubMaterialView *v) {
  if (v->has_friction)
    lgen_set_num(L, "friction", v->friction);
  if (v->has_restitution)
    lgen_set_num(L, "restitution", v->restitution);
  lgen_set_int(L, "material_id", v->material_id);
  (void)L;
  (void)v;
}

static void push_LubMaterialView(lua_State *L, const LubMaterialView *v) {
  lua_createtable(L, 0, 3);
  fill_LubMaterialView(L, v);
}

static void fill_LubManifoldPoint(lua_State *L, const LubManifoldPoint *v) {
  lgen_set_num(L, "x", v->x);
  lgen_set_num(L, "y", v->y);
  lgen_set_num(L, "anchor_a_x", v->anchor_a_x);
  lgen_set_num(L, "anchor_a_y", v->anchor_a_y);
  lgen_set_num(L, "anchor_b_x", v->anchor_b_x);
  lgen_set_num(L, "anchor_b_y", v->anchor_b_y);
  lgen_set_num(L, "separation", v->separation);
  lgen_set_num(L, "normal_impulse", v->normal_impulse);
  lgen_set_num(L, "tangent_impulse", v->tangent_impulse);
  lgen_set_num(L, "total_normal_impulse", v->total_normal_impulse);
  lgen_set_num(L, "normal_velocity", v->normal_velocity);
  lgen_set_int(L, "id", v->id);
  lgen_set_bool(L, "persisted", v->persisted);
  (void)L;
  (void)v;
}

static void push_LubManifoldPoint(lua_State *L, const LubManifoldPoint *v) {
  lua_createtable(L, 0, 13);
  fill_LubManifoldPoint(L, v);
}

static void push_list_LubManifoldPoint(lua_State *L, const LubManifoldPoint *v,
                                       int32_t n) {
  lua_createtable(L, n, 0);
  for (int32_t i = 0; i < n; ++i) {
    push_LubManifoldPoint(L, &v[i]);
    lua_rawseti(L, -2, i + 1);
  }
}

static void fill_LubPreSolveContact(lua_State *L, const LubPreSolveContact *v) {
  {
    push_LubShapeView(L, &v->a);
    lua_setfield(L, -2, "a");
  }
  {
    push_LubShapeView(L, &v->b);
    lua_setfield(L, -2, "b");
  }
  lgen_set_num(L, "nx", v->nx);
  lgen_set_num(L, "ny", v->ny);
  lgen_set_num(L, "rolling_impulse", v->rolling_impulse);
  lgen_set_int(L, "point_count", v->point_count);
  if (v->points) {
    push_list_LubManifoldPoint(L, v->points, v->points_count);
    lua_setfield(L, -2, "points");
  }
  if (v->has_x)
    lgen_set_num(L, "x", v->x);
  if (v->has_y)
    lgen_set_num(L, "y", v->y);
  if (v->has_separation)
    lgen_set_num(L, "separation", v->separation);
  if (v->has_normal_velocity)
    lgen_set_num(L, "normal_velocity", v->normal_velocity);
  (void)L;
  (void)v;
}

static void push_LubPreSolveContact(lua_State *L, const LubPreSolveContact *v) {
  lua_createtable(L, 0, 11);
  fill_LubPreSolveContact(L, v);
}

static void fill_LubDebugData(lua_State *L, const LubDebugData *v) {
  if (v->segments) {
    lgen_push_float_view(L, v->segments, v->segments_count);
    lua_setfield(L, -2, "segments");
  }
  if (v->circles) {
    lgen_push_float_view(L, v->circles, v->circles_count);
    lua_setfield(L, -2, "circles");
  }
  if (v->capsules) {
    lgen_push_float_view(L, v->capsules, v->capsules_count);
    lua_setfield(L, -2, "capsules");
  }
  if (v->polygons) {
    lgen_push_float_view(L, v->polygons, v->polygons_count);
    lua_setfield(L, -2, "polygons");
  }
  if (v->points) {
    lgen_push_float_view(L, v->points, v->points_count);
    lua_setfield(L, -2, "points");
  }
  (void)L;
  (void)v;
}

static void push_LubDebugData(lua_State *L, const LubDebugData *v) {
  lua_createtable(L, 0, 5);
  fill_LubDebugData(L, v);
}

static void fill_LubPose(lua_State *L, const LubPose *v) {
  lgen_set_num(L, "x", v->x);
  lgen_set_num(L, "y", v->y);
  lgen_set_num(L, "angle", v->angle);
  lgen_set_num(L, "vx", v->vx);
  lgen_set_num(L, "vy", v->vy);
  lgen_set_num(L, "w", v->w);
  lgen_set_bool(L, "awake", v->awake);
  lgen_set_bool(L, "enabled", v->enabled);
  lgen_set_bool(L, "sleep", v->sleep);
  lgen_set_num(L, "sleep_threshold", v->sleep_threshold);
  (void)L;
  (void)v;
}

static void push_LubPose(lua_State *L, const LubPose *v) {
  lua_createtable(L, 0, 10);
  fill_LubPose(L, v);
}

static void fill_LubVelocity(lua_State *L, const LubVelocity *v) {
  lgen_set_num(L, "x", v->x);
  lgen_set_num(L, "y", v->y);
  lgen_set_num(L, "w", v->w);
  (void)L;
  (void)v;
}

static void push_LubVelocity(lua_State *L, const LubVelocity *v) {
  lua_createtable(L, 0, 3);
  fill_LubVelocity(L, v);
}

static void fill_LubMassData(lua_State *L, const LubMassData *v) {
  lgen_set_num(L, "mass", v->mass);
  lgen_set_num(L, "inertia", v->inertia);
  {
    push_LubVec2d(L, &v->center);
    lua_setfield(L, -2, "center");
  }
  {
    push_LubVec2d(L, &v->local_center);
    lua_setfield(L, -2, "local_center");
  }
  (void)L;
  (void)v;
}

static void push_LubMassData(lua_State *L, const LubMassData *v) {
  lua_createtable(L, 0, 4);
  fill_LubMassData(L, v);
}

static void fill_LubAabb(lua_State *L, const LubAabb *v) {
  lgen_set_num(L, "min_x", v->min_x);
  lgen_set_num(L, "min_y", v->min_y);
  lgen_set_num(L, "max_x", v->max_x);
  lgen_set_num(L, "max_y", v->max_y);
  (void)L;
  (void)v;
}

static void push_LubAabb(lua_State *L, const LubAabb *v) {
  lua_createtable(L, 0, 4);
  fill_LubAabb(L, v);
}

static void fill_LubFilterInfo(lua_State *L, const LubFilterInfo *v) {
  lgen_set_bits(L, "category_bits", v->category_bits);
  lgen_set_bits(L, "mask_bits", v->mask_bits);
  lgen_set_int(L, "group", v->group);
  (void)L;
  (void)v;
}

static void push_LubFilterInfo(lua_State *L, const LubFilterInfo *v) {
  lua_createtable(L, 0, 3);
  fill_LubFilterInfo(L, v);
}

static void fill_LubShapeInfo(lua_State *L, const LubShapeInfo *v) {
  fill_LubShapeView(L, &v->base);
  lgen_set_num(L, "density", v->density);
  lgen_set_num(L, "friction", v->friction);
  lgen_set_num(L, "restitution", v->restitution);
  lgen_set_bool(L, "sensor", v->sensor);
  lgen_set_bool(L, "sensor_events", v->sensor_events);
  lgen_set_bool(L, "contact", v->contact);
  lgen_set_bool(L, "pre_solve", v->pre_solve);
  lgen_set_bool(L, "hit", v->hit);
  {
    push_LubFilterInfo(L, &v->filter);
    lua_setfield(L, -2, "filter");
  }
  {
    push_LubAabb(L, &v->aabb);
    lua_setfield(L, -2, "aabb");
  }
  (void)L;
  (void)v;
}

static void push_LubShapeInfo(lua_State *L, const LubShapeInfo *v) {
  lua_createtable(L, 0, 22);
  fill_LubShapeInfo(L, v);
}

static void fill_LubWorldCallbackInfo(lua_State *L,
                                      const LubWorldCallbackInfo *v) {
  lgen_set_bool(L, "filter", v->filter);
  lgen_set_bool(L, "pre_solve", v->pre_solve);
  lgen_set_bool(L, "friction", v->friction);
  lgen_set_bool(L, "restitution", v->restitution);
  (void)L;
  (void)v;
}

static void push_LubWorldCallbackInfo(lua_State *L,
                                      const LubWorldCallbackInfo *v) {
  lua_createtable(L, 0, 4);
  fill_LubWorldCallbackInfo(L, v);
}

static void fill_LubWorldInfo(lua_State *L, const LubWorldInfo *v) {
  lgen_set_str(L, "key", v->key);
  lgen_set_bool(L, "valid", v->valid);
  lgen_set_int(L, "version", v->version);
  lgen_set_int(L, "generation", v->generation);
  lgen_set_bool(L, "begun", v->begun);
  lgen_set_bool(L, "prune", v->prune);
  lgen_set_num(L, "fixed_dt", v->fixed_dt);
  lgen_set_int(L, "substeps", v->substeps);
  lgen_set_int(L, "max_steps", v->max_steps);
  lgen_set_num(L, "accumulator", v->accumulator);
  lgen_set_int(L, "pending_commands", v->pending_commands);
  {
    push_LubWorldCallbackInfo(L, &v->callbacks);
    lua_setfield(L, -2, "callbacks");
  }
  if (v->has_gravity) {
    push_LubVec2d(L, &v->gravity);
    lua_setfield(L, -2, "gravity");
  }
  if (v->has_sleep)
    lgen_set_bool(L, "sleep", v->sleep);
  if (v->has_continuous)
    lgen_set_bool(L, "continuous", v->continuous);
  if (v->has_warm_starting)
    lgen_set_bool(L, "warm_starting", v->warm_starting);
  if (v->has_restitution_threshold)
    lgen_set_num(L, "restitution_threshold", v->restitution_threshold);
  if (v->has_hit_event_threshold)
    lgen_set_num(L, "hit_event_threshold", v->hit_event_threshold);
  if (v->has_maximum_linear_speed)
    lgen_set_num(L, "maximum_linear_speed", v->maximum_linear_speed);
  if (v->has_awake_body_count)
    lgen_set_int(L, "awake_body_count", v->awake_body_count);
  (void)L;
  (void)v;
}

static void push_LubWorldInfo(lua_State *L, const LubWorldInfo *v) {
  lua_createtable(L, 0, 20);
  fill_LubWorldInfo(L, v);
}

static void fill_LubStepInfo(lua_State *L, const LubStepInfo *v) {
  lgen_set_int(L, "steps", v->steps);
  lgen_set_int(L, "commands", v->commands);
  lgen_set_num(L, "alpha", v->alpha);
  lgen_set_bool(L, "dropped", v->dropped);
  lgen_set_int(L, "contact_begins", v->contact_begins);
  lgen_set_int(L, "contact_ends", v->contact_ends);
  lgen_set_int(L, "contact_hits", v->contact_hits);
  lgen_set_int(L, "sensor_begins", v->sensor_begins);
  lgen_set_int(L, "sensor_ends", v->sensor_ends);
  lgen_set_int(L, "body_moves", v->body_moves);
  lgen_set_int(L, "body_events", v->body_events);
  (void)L;
  (void)v;
}

static void push_LubStepInfo(lua_State *L, const LubStepInfo *v) {
  lua_createtable(L, 0, 11);
  fill_LubStepInfo(L, v);
}

static void fill_LubJointView(lua_State *L, const LubJointView *v) {
  lgen_set_str(L, "joint", v->joint);
  if (name_LubPhys2dJointType(v->type)) {
    lua_pushstring(L, name_LubPhys2dJointType(v->type));
    lua_setfield(L, -2, "type");
  }
  lgen_set_str(L, "a", v->a);
  lgen_set_str(L, "b", v->b);
  lgen_set_bool(L, "valid", v->valid);
  (void)L;
  (void)v;
}

static void push_LubJointView(lua_State *L, const LubJointView *v) {
  lua_createtable(L, 0, 5);
  fill_LubJointView(L, v);
}

static void push_list_LubJointView(lua_State *L, const LubJointView *v,
                                   int32_t n) {
  lua_createtable(L, n, 0);
  for (int32_t i = 0; i < n; ++i) {
    push_LubJointView(L, &v[i]);
    lua_rawseti(L, -2, i + 1);
  }
}

static void fill_LubJointInfo(lua_State *L, const LubJointInfo *v) {
  fill_LubJointView(L, &v->base);
  lgen_set_bool(L, "collide_connected", v->collide_connected);
  {
    push_LubVec2d(L, &v->force);
    lua_setfield(L, -2, "force");
  }
  lgen_set_num(L, "torque", v->torque);
  lgen_set_num(L, "linear_separation", v->linear_separation);
  lgen_set_num(L, "angular_separation", v->angular_separation);
  if (v->has_local_anchor_a) {
    push_LubVec2d(L, &v->local_anchor_a);
    lua_setfield(L, -2, "local_anchor_a");
  }
  if (v->has_local_anchor_b) {
    push_LubVec2d(L, &v->local_anchor_b);
    lua_setfield(L, -2, "local_anchor_b");
  }
  if (v->has_local_axis_a) {
    push_LubVec2d(L, &v->local_axis_a);
    lua_setfield(L, -2, "local_axis_a");
  }
  if (v->has_reference_angle)
    lgen_set_num(L, "reference_angle", v->reference_angle);
  (void)L;
  (void)v;
}

static void push_LubJointInfo(lua_State *L, const LubJointInfo *v) {
  lua_createtable(L, 0, 14);
  fill_LubJointInfo(L, v);
}

static void fill_LubContactData(lua_State *L, const LubContactData *v) {
  {
    push_LubShapeView(L, &v->a);
    lua_setfield(L, -2, "a");
  }
  {
    push_LubShapeView(L, &v->b);
    lua_setfield(L, -2, "b");
  }
  lgen_set_num(L, "nx", v->nx);
  lgen_set_num(L, "ny", v->ny);
  lgen_set_int(L, "point_count", v->point_count);
  if (v->has_x)
    lgen_set_num(L, "x", v->x);
  if (v->has_y)
    lgen_set_num(L, "y", v->y);
  if (v->has_separation)
    lgen_set_num(L, "separation", v->separation);
  (void)L;
  (void)v;
}

static void push_LubContactData(lua_State *L, const LubContactData *v) {
  lua_createtable(L, 0, 8);
  fill_LubContactData(L, v);
}

static void push_list_LubContactData(lua_State *L, const LubContactData *v,
                                     int32_t n) {
  lua_createtable(L, n, 0);
  for (int32_t i = 0; i < n; ++i) {
    push_LubContactData(L, &v[i]);
    lua_rawseti(L, -2, i + 1);
  }
}

static void fill_LubContactEvent(lua_State *L, const LubContactEvent *v) {
  {
    push_LubShapeView(L, &v->a);
    lua_setfield(L, -2, "a");
  }
  {
    push_LubShapeView(L, &v->b);
    lua_setfield(L, -2, "b");
  }
  lgen_set_num(L, "nx", v->nx);
  lgen_set_num(L, "ny", v->ny);
  lgen_set_int(L, "point_count", v->point_count);
  lgen_set_num(L, "x", v->x);
  lgen_set_num(L, "y", v->y);
  if (v->has_approach_speed)
    lgen_set_num(L, "approach_speed", v->approach_speed);
  (void)L;
  (void)v;
}

static void push_LubContactEvent(lua_State *L, const LubContactEvent *v) {
  lua_createtable(L, 0, 8);
  fill_LubContactEvent(L, v);
}

static void push_list_LubContactEvent(lua_State *L, const LubContactEvent *v,
                                      int32_t n) {
  lua_createtable(L, n, 0);
  for (int32_t i = 0; i < n; ++i) {
    push_LubContactEvent(L, &v[i]);
    lua_rawseti(L, -2, i + 1);
  }
}

static void fill_LubSensorEvent(lua_State *L, const LubSensorEvent *v) {
  {
    push_LubShapeView(L, &v->sensor);
    lua_setfield(L, -2, "sensor");
  }
  {
    push_LubShapeView(L, &v->visitor);
    lua_setfield(L, -2, "visitor");
  }
  (void)L;
  (void)v;
}

static void push_LubSensorEvent(lua_State *L, const LubSensorEvent *v) {
  lua_createtable(L, 0, 2);
  fill_LubSensorEvent(L, v);
}

static void push_list_LubSensorEvent(lua_State *L, const LubSensorEvent *v,
                                     int32_t n) {
  lua_createtable(L, n, 0);
  for (int32_t i = 0; i < n; ++i) {
    push_LubSensorEvent(L, &v[i]);
    lua_rawseti(L, -2, i + 1);
  }
}

static void fill_LubBodyEvent(lua_State *L, const LubBodyEvent *v) {
  lgen_set_str(L, "body", v->body);
  lgen_set_bool(L, "valid", v->valid);
  lgen_set_num(L, "x", v->x);
  lgen_set_num(L, "y", v->y);
  lgen_set_num(L, "angle", v->angle);
  lgen_set_bool(L, "fell_asleep", v->fell_asleep);
  (void)L;
  (void)v;
}

static void push_LubBodyEvent(lua_State *L, const LubBodyEvent *v) {
  lua_createtable(L, 0, 6);
  fill_LubBodyEvent(L, v);
}

static void push_list_LubBodyEvent(lua_State *L, const LubBodyEvent *v,
                                   int32_t n) {
  lua_createtable(L, n, 0);
  for (int32_t i = 0; i < n; ++i) {
    push_LubBodyEvent(L, &v[i]);
    lua_rawseti(L, -2, i + 1);
  }
}

static void fill_LubRayHit(lua_State *L, const LubRayHit *v) {
  fill_LubShapeView(L, &v->base);
  lgen_set_num(L, "x", v->x);
  lgen_set_num(L, "y", v->y);
  lgen_set_num(L, "nx", v->nx);
  lgen_set_num(L, "ny", v->ny);
  lgen_set_num(L, "fraction", v->fraction);
  if (v->has_node_visits)
    lgen_set_int(L, "node_visits", v->node_visits);
  if (v->has_leaf_visits)
    lgen_set_int(L, "leaf_visits", v->leaf_visits);
  (void)L;
  (void)v;
}

static void push_LubRayHit(lua_State *L, const LubRayHit *v) {
  lua_createtable(L, 0, 19);
  fill_LubRayHit(L, v);
}

static void push_list_LubRayHit(lua_State *L, const LubRayHit *v, int32_t n) {
  lua_createtable(L, n, 0);
  for (int32_t i = 0; i < n; ++i) {
    push_LubRayHit(L, &v[i]);
    lua_rawseti(L, -2, i + 1);
  }
}

static void fill_LubShapeRayHit(lua_State *L, const LubShapeRayHit *v) {
  lgen_set_num(L, "x", v->x);
  lgen_set_num(L, "y", v->y);
  lgen_set_num(L, "nx", v->nx);
  lgen_set_num(L, "ny", v->ny);
  lgen_set_num(L, "fraction", v->fraction);
  lgen_set_int(L, "iterations", v->iterations);
  (void)L;
  (void)v;
}

static void push_LubShapeRayHit(lua_State *L, const LubShapeRayHit *v) {
  lua_createtable(L, 0, 6);
  fill_LubShapeRayHit(L, v);
}

static void fill_LubMoverCast(lua_State *L, const LubMoverCast *v) {
  lgen_set_num(L, "fraction", v->fraction);
  lgen_set_num(L, "dx", v->dx);
  lgen_set_num(L, "dy", v->dy);
  (void)L;
  (void)v;
}

static void push_LubMoverCast(lua_State *L, const LubMoverCast *v) {
  lua_createtable(L, 0, 3);
  fill_LubMoverCast(L, v);
}

static void fill_LubMoverPlane(lua_State *L, const LubMoverPlane *v) {
  fill_LubShapeView(L, &v->base);
  lgen_set_bool(L, "hit", v->hit);
  lgen_set_num(L, "x", v->x);
  lgen_set_num(L, "y", v->y);
  lgen_set_num(L, "nx", v->nx);
  lgen_set_num(L, "ny", v->ny);
  lgen_set_num(L, "offset", v->offset);
  (void)L;
  (void)v;
}

static void push_LubMoverPlane(lua_State *L, const LubMoverPlane *v) {
  lua_createtable(L, 0, 18);
  fill_LubMoverPlane(L, v);
}

static void push_list_LubMoverPlane(lua_State *L, const LubMoverPlane *v,
                                    int32_t n) {
  lua_createtable(L, n, 0);
  for (int32_t i = 0; i < n; ++i) {
    push_LubMoverPlane(L, &v[i]);
    lua_rawseti(L, -2, i + 1);
  }
}

static void fill_LubProfile(lua_State *L, const LubProfile *v) {
  lgen_set_num(L, "step", v->step);
  lgen_set_num(L, "pairs", v->pairs);
  lgen_set_num(L, "collide", v->collide);
  lgen_set_num(L, "solve", v->solve);
  lgen_set_num(L, "merge_islands", v->merge_islands);
  lgen_set_num(L, "prepare_stages", v->prepare_stages);
  lgen_set_num(L, "solve_constraints", v->solve_constraints);
  lgen_set_num(L, "prepare_constraints", v->prepare_constraints);
  lgen_set_num(L, "integrate_velocities", v->integrate_velocities);
  lgen_set_num(L, "warm_start", v->warm_start);
  lgen_set_num(L, "solve_impulses", v->solve_impulses);
  lgen_set_num(L, "integrate_positions", v->integrate_positions);
  lgen_set_num(L, "relax_impulses", v->relax_impulses);
  lgen_set_num(L, "apply_restitution", v->apply_restitution);
  lgen_set_num(L, "store_impulses", v->store_impulses);
  lgen_set_num(L, "split_islands", v->split_islands);
  lgen_set_num(L, "transforms", v->transforms);
  lgen_set_num(L, "hit_events", v->hit_events);
  lgen_set_num(L, "refit", v->refit);
  lgen_set_num(L, "bullets", v->bullets);
  lgen_set_num(L, "sleep_islands", v->sleep_islands);
  lgen_set_num(L, "sensors", v->sensors);
  (void)L;
  (void)v;
}

static void push_LubProfile(lua_State *L, const LubProfile *v) {
  lua_createtable(L, 0, 22);
  fill_LubProfile(L, v);
}

static void fill_LubCounters(lua_State *L, const LubCounters *v) {
  lgen_set_int(L, "body_count", v->body_count);
  lgen_set_int(L, "shape_count", v->shape_count);
  lgen_set_int(L, "contact_count", v->contact_count);
  lgen_set_int(L, "joint_count", v->joint_count);
  lgen_set_int(L, "island_count", v->island_count);
  lgen_set_int(L, "stack_used", v->stack_used);
  lgen_set_int(L, "static_tree_height", v->static_tree_height);
  lgen_set_int(L, "tree_height", v->tree_height);
  lgen_set_int(L, "byte_count", v->byte_count);
  lgen_set_int(L, "task_count", v->task_count);
  {
    lgen_push_int_table(L, v->color_counts, v->color_counts_count);
    lua_setfield(L, -2, "color_counts");
  }
  (void)L;
  (void)v;
}

static void push_LubCounters(lua_State *L, const LubCounters *v) {
  lua_createtable(L, 0, 11);
  fill_LubCounters(L, v);
}

static void fill_LubVec3d(lua_State *L, const LubVec3d *v) {
  lgen_set_num(L, "x", v->x);
  lgen_set_num(L, "y", v->y);
  lgen_set_num(L, "z", v->z);
  (void)L;
  (void)v;
}

static void push_LubVec3d(lua_State *L, const LubVec3d *v) {
  lua_createtable(L, 0, 3);
  fill_LubVec3d(L, v);
}

static void fill_LubShapeView3d(lua_State *L, const LubShapeView3d *v) {
  lgen_set_str(L, "body", v->body);
  lgen_set_str(L, "shape", v->shape);
  if (v->tag.len > 0)
    lgen_set_str(L, "tag", v->tag);
  if (v->material_name.len > 0)
    lgen_set_str(L, "material_name", v->material_name);
  if (v->has_material_id)
    lgen_set_int(L, "material_id", v->material_id);
  if (v->has_kind && name_LubPhys3dShapeKind(v->kind)) {
    lua_pushstring(L, name_LubPhys3dShapeKind(v->kind));
    lua_setfield(L, -2, "kind");
  }
  if (v->has_category_bits)
    lgen_set_bits(L, "category_bits", v->category_bits);
  if (v->has_mask_bits)
    lgen_set_bits(L, "mask_bits", v->mask_bits);
  if (v->has_group)
    lgen_set_int(L, "group", v->group);
  lgen_set_bool(L, "valid", v->valid);
  (void)L;
  (void)v;
}

static void push_LubShapeView3d(lua_State *L, const LubShapeView3d *v) {
  lua_createtable(L, 0, 10);
  fill_LubShapeView3d(L, v);
}

static void push_list_LubShapeView3d(lua_State *L, const LubShapeView3d *v,
                                     int32_t n) {
  lua_createtable(L, n, 0);
  for (int32_t i = 0; i < n; ++i) {
    push_LubShapeView3d(L, &v[i]);
    lua_rawseti(L, -2, i + 1);
  }
}

static void fill_LubPreSolveContact3d(lua_State *L,
                                      const LubPreSolveContact3d *v) {
  {
    push_LubShapeView3d(L, &v->a);
    lua_setfield(L, -2, "a");
  }
  {
    push_LubShapeView3d(L, &v->b);
    lua_setfield(L, -2, "b");
  }
  lgen_set_num(L, "x", v->x);
  lgen_set_num(L, "y", v->y);
  lgen_set_num(L, "z", v->z);
  lgen_set_num(L, "nx", v->nx);
  lgen_set_num(L, "ny", v->ny);
  lgen_set_num(L, "nz", v->nz);
  (void)L;
  (void)v;
}

static void push_LubPreSolveContact3d(lua_State *L,
                                      const LubPreSolveContact3d *v) {
  lua_createtable(L, 0, 8);
  fill_LubPreSolveContact3d(L, v);
}

static void fill_LubPose3d(lua_State *L, const LubPose3d *v) {
  lgen_set_num(L, "x", v->x);
  lgen_set_num(L, "y", v->y);
  lgen_set_num(L, "z", v->z);
  lgen_set_num(L, "qx", v->qx);
  lgen_set_num(L, "qy", v->qy);
  lgen_set_num(L, "qz", v->qz);
  lgen_set_num(L, "qw", v->qw);
  lgen_set_num(L, "vx", v->vx);
  lgen_set_num(L, "vy", v->vy);
  lgen_set_num(L, "vz", v->vz);
  lgen_set_num(L, "wx", v->wx);
  lgen_set_num(L, "wy", v->wy);
  lgen_set_num(L, "wz", v->wz);
  lgen_set_bool(L, "awake", v->awake);
  lgen_set_bool(L, "enabled", v->enabled);
  lgen_set_bool(L, "sleep", v->sleep);
  lgen_set_num(L, "sleep_threshold", v->sleep_threshold);
  (void)L;
  (void)v;
}

static void push_LubPose3d(lua_State *L, const LubPose3d *v) {
  lua_createtable(L, 0, 17);
  fill_LubPose3d(L, v);
}

static void fill_LubVelocity3d(lua_State *L, const LubVelocity3d *v) {
  lgen_set_num(L, "x", v->x);
  lgen_set_num(L, "y", v->y);
  lgen_set_num(L, "z", v->z);
  lgen_set_num(L, "wx", v->wx);
  lgen_set_num(L, "wy", v->wy);
  lgen_set_num(L, "wz", v->wz);
  (void)L;
  (void)v;
}

static void push_LubVelocity3d(lua_State *L, const LubVelocity3d *v) {
  lua_createtable(L, 0, 6);
  fill_LubVelocity3d(L, v);
}

static void fill_LubInertia3d(lua_State *L, const LubInertia3d *v) {
  lgen_set_num(L, "xx", v->xx);
  lgen_set_num(L, "yy", v->yy);
  lgen_set_num(L, "zz", v->zz);
  lgen_set_num(L, "xy", v->xy);
  lgen_set_num(L, "xz", v->xz);
  lgen_set_num(L, "yz", v->yz);
  (void)L;
  (void)v;
}

static void push_LubInertia3d(lua_State *L, const LubInertia3d *v) {
  lua_createtable(L, 0, 6);
  fill_LubInertia3d(L, v);
}

static void fill_LubMassData3d(lua_State *L, const LubMassData3d *v) {
  lgen_set_num(L, "mass", v->mass);
  {
    push_LubVec3d(L, &v->center);
    lua_setfield(L, -2, "center");
  }
  {
    push_LubVec3d(L, &v->local_center);
    lua_setfield(L, -2, "local_center");
  }
  {
    push_LubInertia3d(L, &v->inertia);
    lua_setfield(L, -2, "inertia");
  }
  (void)L;
  (void)v;
}

static void push_LubMassData3d(lua_State *L, const LubMassData3d *v) {
  lua_createtable(L, 0, 4);
  fill_LubMassData3d(L, v);
}

static void fill_LubAabb3d(lua_State *L, const LubAabb3d *v) {
  lgen_set_num(L, "min_x", v->min_x);
  lgen_set_num(L, "min_y", v->min_y);
  lgen_set_num(L, "min_z", v->min_z);
  lgen_set_num(L, "max_x", v->max_x);
  lgen_set_num(L, "max_y", v->max_y);
  lgen_set_num(L, "max_z", v->max_z);
  (void)L;
  (void)v;
}

static void push_LubAabb3d(lua_State *L, const LubAabb3d *v) {
  lua_createtable(L, 0, 6);
  fill_LubAabb3d(L, v);
}

static void fill_LubShapeInfo3d(lua_State *L, const LubShapeInfo3d *v) {
  fill_LubShapeView3d(L, &v->base);
  lgen_set_num(L, "density", v->density);
  lgen_set_num(L, "friction", v->friction);
  lgen_set_num(L, "restitution", v->restitution);
  lgen_set_bool(L, "sensor", v->sensor);
  lgen_set_bool(L, "sensor_events", v->sensor_events);
  lgen_set_bool(L, "contact", v->contact);
  lgen_set_bool(L, "pre_solve", v->pre_solve);
  lgen_set_bool(L, "hit", v->hit);
  {
    push_LubFilterInfo(L, &v->filter);
    lua_setfield(L, -2, "filter");
  }
  {
    push_LubAabb3d(L, &v->aabb);
    lua_setfield(L, -2, "aabb");
  }
  (void)L;
  (void)v;
}

static void push_LubShapeInfo3d(lua_State *L, const LubShapeInfo3d *v) {
  lua_createtable(L, 0, 20);
  fill_LubShapeInfo3d(L, v);
}

static void fill_LubWorldInfo3d(lua_State *L, const LubWorldInfo3d *v) {
  lgen_set_str(L, "key", v->key);
  lgen_set_bool(L, "valid", v->valid);
  lgen_set_int(L, "version", v->version);
  lgen_set_int(L, "generation", v->generation);
  lgen_set_bool(L, "begun", v->begun);
  lgen_set_bool(L, "prune", v->prune);
  lgen_set_num(L, "fixed_dt", v->fixed_dt);
  lgen_set_int(L, "substeps", v->substeps);
  lgen_set_int(L, "max_steps", v->max_steps);
  lgen_set_num(L, "accumulator", v->accumulator);
  lgen_set_int(L, "pending_commands", v->pending_commands);
  if (v->has_gravity) {
    push_LubVec3d(L, &v->gravity);
    lua_setfield(L, -2, "gravity");
  }
  if (v->has_sleep)
    lgen_set_bool(L, "sleep", v->sleep);
  if (v->has_continuous)
    lgen_set_bool(L, "continuous", v->continuous);
  if (v->has_warm_starting)
    lgen_set_bool(L, "warm_starting", v->warm_starting);
  if (v->has_restitution_threshold)
    lgen_set_num(L, "restitution_threshold", v->restitution_threshold);
  if (v->has_hit_event_threshold)
    lgen_set_num(L, "hit_event_threshold", v->hit_event_threshold);
  if (v->has_maximum_linear_speed)
    lgen_set_num(L, "maximum_linear_speed", v->maximum_linear_speed);
  if (v->has_awake_body_count)
    lgen_set_int(L, "awake_body_count", v->awake_body_count);
  (void)L;
  (void)v;
}

static void push_LubWorldInfo3d(lua_State *L, const LubWorldInfo3d *v) {
  lua_createtable(L, 0, 19);
  fill_LubWorldInfo3d(L, v);
}

static void fill_LubStepInfo3d(lua_State *L, const LubStepInfo3d *v) {
  fill_LubStepInfo(L, &v->base);
  lgen_set_int(L, "joint_events", v->joint_events);
  (void)L;
  (void)v;
}

static void push_LubStepInfo3d(lua_State *L, const LubStepInfo3d *v) {
  lua_createtable(L, 0, 12);
  fill_LubStepInfo3d(L, v);
}

static void fill_LubFrame3d(lua_State *L, const LubFrame3d *v) {
  lgen_set_num(L, "x", v->x);
  lgen_set_num(L, "y", v->y);
  lgen_set_num(L, "z", v->z);
  lgen_set_num(L, "qx", v->qx);
  lgen_set_num(L, "qy", v->qy);
  lgen_set_num(L, "qz", v->qz);
  lgen_set_num(L, "qw", v->qw);
  (void)L;
  (void)v;
}

static void push_LubFrame3d(lua_State *L, const LubFrame3d *v) {
  lua_createtable(L, 0, 7);
  fill_LubFrame3d(L, v);
}

static void fill_LubJointView3d(lua_State *L, const LubJointView3d *v) {
  lgen_set_str(L, "joint", v->joint);
  if (name_LubPhys3dJointType(v->type)) {
    lua_pushstring(L, name_LubPhys3dJointType(v->type));
    lua_setfield(L, -2, "type");
  }
  lgen_set_str(L, "a", v->a);
  lgen_set_str(L, "b", v->b);
  lgen_set_bool(L, "valid", v->valid);
  (void)L;
  (void)v;
}

static void push_LubJointView3d(lua_State *L, const LubJointView3d *v) {
  lua_createtable(L, 0, 5);
  fill_LubJointView3d(L, v);
}

static void push_list_LubJointView3d(lua_State *L, const LubJointView3d *v,
                                     int32_t n) {
  lua_createtable(L, n, 0);
  for (int32_t i = 0; i < n; ++i) {
    push_LubJointView3d(L, &v[i]);
    lua_rawseti(L, -2, i + 1);
  }
}

static void fill_LubJointInfo3d(lua_State *L, const LubJointInfo3d *v) {
  fill_LubJointView3d(L, &v->base);
  lgen_set_bool(L, "collide_connected", v->collide_connected);
  {
    push_LubVec3d(L, &v->force);
    lua_setfield(L, -2, "force");
  }
  {
    push_LubVec3d(L, &v->torque);
    lua_setfield(L, -2, "torque");
  }
  lgen_set_num(L, "linear_separation", v->linear_separation);
  lgen_set_num(L, "angular_separation", v->angular_separation);
  {
    push_LubFrame3d(L, &v->local_frame_a);
    lua_setfield(L, -2, "local_frame_a");
  }
  {
    push_LubFrame3d(L, &v->local_frame_b);
    lua_setfield(L, -2, "local_frame_b");
  }
  (void)L;
  (void)v;
}

static void push_LubJointInfo3d(lua_State *L, const LubJointInfo3d *v) {
  lua_createtable(L, 0, 12);
  fill_LubJointInfo3d(L, v);
}

static void fill_LubContactData3d(lua_State *L, const LubContactData3d *v) {
  {
    push_LubShapeView3d(L, &v->a);
    lua_setfield(L, -2, "a");
  }
  {
    push_LubShapeView3d(L, &v->b);
    lua_setfield(L, -2, "b");
  }
  lgen_set_num(L, "nx", v->nx);
  lgen_set_num(L, "ny", v->ny);
  lgen_set_num(L, "nz", v->nz);
  lgen_set_int(L, "manifold_count", v->manifold_count);
  lgen_set_int(L, "point_count", v->point_count);
  if (v->has_x)
    lgen_set_num(L, "x", v->x);
  if (v->has_y)
    lgen_set_num(L, "y", v->y);
  if (v->has_z)
    lgen_set_num(L, "z", v->z);
  if (v->has_separation)
    lgen_set_num(L, "separation", v->separation);
  (void)L;
  (void)v;
}

static void push_LubContactData3d(lua_State *L, const LubContactData3d *v) {
  lua_createtable(L, 0, 11);
  fill_LubContactData3d(L, v);
}

static void push_list_LubContactData3d(lua_State *L, const LubContactData3d *v,
                                       int32_t n) {
  lua_createtable(L, n, 0);
  for (int32_t i = 0; i < n; ++i) {
    push_LubContactData3d(L, &v[i]);
    lua_rawseti(L, -2, i + 1);
  }
}

static void fill_LubContactEvent3d(lua_State *L, const LubContactEvent3d *v) {
  {
    push_LubShapeView3d(L, &v->a);
    lua_setfield(L, -2, "a");
  }
  {
    push_LubShapeView3d(L, &v->b);
    lua_setfield(L, -2, "b");
  }
  lgen_set_num(L, "nx", v->nx);
  lgen_set_num(L, "ny", v->ny);
  lgen_set_num(L, "nz", v->nz);
  lgen_set_int(L, "point_count", v->point_count);
  lgen_set_num(L, "x", v->x);
  lgen_set_num(L, "y", v->y);
  lgen_set_num(L, "z", v->z);
  if (v->has_approach_speed)
    lgen_set_num(L, "approach_speed", v->approach_speed);
  (void)L;
  (void)v;
}

static void push_LubContactEvent3d(lua_State *L, const LubContactEvent3d *v) {
  lua_createtable(L, 0, 10);
  fill_LubContactEvent3d(L, v);
}

static void push_list_LubContactEvent3d(lua_State *L,
                                        const LubContactEvent3d *v, int32_t n) {
  lua_createtable(L, n, 0);
  for (int32_t i = 0; i < n; ++i) {
    push_LubContactEvent3d(L, &v[i]);
    lua_rawseti(L, -2, i + 1);
  }
}

static void fill_LubSensorEvent3d(lua_State *L, const LubSensorEvent3d *v) {
  {
    push_LubShapeView3d(L, &v->sensor);
    lua_setfield(L, -2, "sensor");
  }
  {
    push_LubShapeView3d(L, &v->visitor);
    lua_setfield(L, -2, "visitor");
  }
  (void)L;
  (void)v;
}

static void push_LubSensorEvent3d(lua_State *L, const LubSensorEvent3d *v) {
  lua_createtable(L, 0, 2);
  fill_LubSensorEvent3d(L, v);
}

static void push_list_LubSensorEvent3d(lua_State *L, const LubSensorEvent3d *v,
                                       int32_t n) {
  lua_createtable(L, n, 0);
  for (int32_t i = 0; i < n; ++i) {
    push_LubSensorEvent3d(L, &v[i]);
    lua_rawseti(L, -2, i + 1);
  }
}

static void fill_LubBodyEvent3d(lua_State *L, const LubBodyEvent3d *v) {
  lgen_set_str(L, "body", v->body);
  lgen_set_bool(L, "valid", v->valid);
  lgen_set_num(L, "x", v->x);
  lgen_set_num(L, "y", v->y);
  lgen_set_num(L, "z", v->z);
  lgen_set_num(L, "qx", v->qx);
  lgen_set_num(L, "qy", v->qy);
  lgen_set_num(L, "qz", v->qz);
  lgen_set_num(L, "qw", v->qw);
  lgen_set_bool(L, "fell_asleep", v->fell_asleep);
  (void)L;
  (void)v;
}

static void push_LubBodyEvent3d(lua_State *L, const LubBodyEvent3d *v) {
  lua_createtable(L, 0, 10);
  fill_LubBodyEvent3d(L, v);
}

static void push_list_LubBodyEvent3d(lua_State *L, const LubBodyEvent3d *v,
                                     int32_t n) {
  lua_createtable(L, n, 0);
  for (int32_t i = 0; i < n; ++i) {
    push_LubBodyEvent3d(L, &v[i]);
    lua_rawseti(L, -2, i + 1);
  }
}

static void fill_LubJointEvent3d(lua_State *L, const LubJointEvent3d *v) {
  fill_LubJointView3d(L, &v->base);
  (void)L;
  (void)v;
}

static void push_LubJointEvent3d(lua_State *L, const LubJointEvent3d *v) {
  lua_createtable(L, 0, 5);
  fill_LubJointEvent3d(L, v);
}

static void push_list_LubJointEvent3d(lua_State *L, const LubJointEvent3d *v,
                                      int32_t n) {
  lua_createtable(L, n, 0);
  for (int32_t i = 0; i < n; ++i) {
    push_LubJointEvent3d(L, &v[i]);
    lua_rawseti(L, -2, i + 1);
  }
}

static void fill_LubRayHit3d(lua_State *L, const LubRayHit3d *v) {
  fill_LubShapeView3d(L, &v->base);
  lgen_set_num(L, "x", v->x);
  lgen_set_num(L, "y", v->y);
  lgen_set_num(L, "z", v->z);
  lgen_set_num(L, "nx", v->nx);
  lgen_set_num(L, "ny", v->ny);
  lgen_set_num(L, "nz", v->nz);
  lgen_set_num(L, "fraction", v->fraction);
  lgen_set_int(L, "hit_material_id", v->hit_material_id);
  lgen_set_int(L, "triangle_index", v->triangle_index);
  lgen_set_int(L, "child_index", v->child_index);
  if (v->has_node_visits)
    lgen_set_int(L, "node_visits", v->node_visits);
  if (v->has_leaf_visits)
    lgen_set_int(L, "leaf_visits", v->leaf_visits);
  (void)L;
  (void)v;
}

static void push_LubRayHit3d(lua_State *L, const LubRayHit3d *v) {
  lua_createtable(L, 0, 22);
  fill_LubRayHit3d(L, v);
}

static void push_list_LubRayHit3d(lua_State *L, const LubRayHit3d *v,
                                  int32_t n) {
  lua_createtable(L, n, 0);
  for (int32_t i = 0; i < n; ++i) {
    push_LubRayHit3d(L, &v[i]);
    lua_rawseti(L, -2, i + 1);
  }
}

static void fill_LubShapeRayHit3d(lua_State *L, const LubShapeRayHit3d *v) {
  lgen_set_num(L, "x", v->x);
  lgen_set_num(L, "y", v->y);
  lgen_set_num(L, "z", v->z);
  lgen_set_num(L, "nx", v->nx);
  lgen_set_num(L, "ny", v->ny);
  lgen_set_num(L, "nz", v->nz);
  lgen_set_num(L, "fraction", v->fraction);
  lgen_set_int(L, "iterations", v->iterations);
  lgen_set_int(L, "triangle_index", v->triangle_index);
  lgen_set_int(L, "child_index", v->child_index);
  (void)L;
  (void)v;
}

static void push_LubShapeRayHit3d(lua_State *L, const LubShapeRayHit3d *v) {
  lua_createtable(L, 0, 10);
  fill_LubShapeRayHit3d(L, v);
}

static void fill_LubMoverCast3d(lua_State *L, const LubMoverCast3d *v) {
  lgen_set_num(L, "fraction", v->fraction);
  lgen_set_num(L, "dx", v->dx);
  lgen_set_num(L, "dy", v->dy);
  lgen_set_num(L, "dz", v->dz);
  (void)L;
  (void)v;
}

static void push_LubMoverCast3d(lua_State *L, const LubMoverCast3d *v) {
  lua_createtable(L, 0, 4);
  fill_LubMoverCast3d(L, v);
}

static void fill_LubMoverPlane3d(lua_State *L, const LubMoverPlane3d *v) {
  fill_LubShapeView3d(L, &v->base);
  lgen_set_num(L, "x", v->x);
  lgen_set_num(L, "y", v->y);
  lgen_set_num(L, "z", v->z);
  lgen_set_num(L, "nx", v->nx);
  lgen_set_num(L, "ny", v->ny);
  lgen_set_num(L, "nz", v->nz);
  lgen_set_num(L, "offset", v->offset);
  lgen_set_int(L, "plane_count", v->plane_count);
  (void)L;
  (void)v;
}

static void push_LubMoverPlane3d(lua_State *L, const LubMoverPlane3d *v) {
  lua_createtable(L, 0, 18);
  fill_LubMoverPlane3d(L, v);
}

static void push_list_LubMoverPlane3d(lua_State *L, const LubMoverPlane3d *v,
                                      int32_t n) {
  lua_createtable(L, n, 0);
  for (int32_t i = 0; i < n; ++i) {
    push_LubMoverPlane3d(L, &v[i]);
    lua_rawseti(L, -2, i + 1);
  }
}

static void fill_LubProfile3d(lua_State *L, const LubProfile3d *v) {
  lgen_set_num(L, "step", v->step);
  lgen_set_num(L, "pairs", v->pairs);
  lgen_set_num(L, "collide", v->collide);
  lgen_set_num(L, "solve", v->solve);
  lgen_set_num(L, "solver_setup", v->solver_setup);
  lgen_set_num(L, "constraints", v->constraints);
  lgen_set_num(L, "prepare_constraints", v->prepare_constraints);
  lgen_set_num(L, "integrate_velocities", v->integrate_velocities);
  lgen_set_num(L, "warm_start", v->warm_start);
  lgen_set_num(L, "solve_impulses", v->solve_impulses);
  lgen_set_num(L, "integrate_positions", v->integrate_positions);
  lgen_set_num(L, "relax_impulses", v->relax_impulses);
  lgen_set_num(L, "apply_restitution", v->apply_restitution);
  lgen_set_num(L, "store_impulses", v->store_impulses);
  lgen_set_num(L, "split_islands", v->split_islands);
  lgen_set_num(L, "transforms", v->transforms);
  lgen_set_num(L, "sensor_hits", v->sensor_hits);
  lgen_set_num(L, "joint_events", v->joint_events);
  lgen_set_num(L, "hit_events", v->hit_events);
  lgen_set_num(L, "refit", v->refit);
  lgen_set_num(L, "bullets", v->bullets);
  lgen_set_num(L, "sleep_islands", v->sleep_islands);
  lgen_set_num(L, "sensors", v->sensors);
  (void)L;
  (void)v;
}

static void push_LubProfile3d(lua_State *L, const LubProfile3d *v) {
  lua_createtable(L, 0, 23);
  fill_LubProfile3d(L, v);
}

static void fill_LubCounters3d(lua_State *L, const LubCounters3d *v) {
  lgen_set_int(L, "body_count", v->body_count);
  lgen_set_int(L, "shape_count", v->shape_count);
  lgen_set_int(L, "contact_count", v->contact_count);
  lgen_set_int(L, "joint_count", v->joint_count);
  lgen_set_int(L, "island_count", v->island_count);
  lgen_set_int(L, "stack_used", v->stack_used);
  lgen_set_int(L, "arena_capacity", v->arena_capacity);
  lgen_set_int(L, "static_tree_height", v->static_tree_height);
  lgen_set_int(L, "tree_height", v->tree_height);
  lgen_set_int(L, "sat_call_count", v->sat_call_count);
  lgen_set_int(L, "sat_cache_hit_count", v->sat_cache_hit_count);
  lgen_set_int(L, "byte_count", v->byte_count);
  lgen_set_int(L, "task_count", v->task_count);
  lgen_set_int(L, "awake_contact_count", v->awake_contact_count);
  lgen_set_int(L, "recycled_contact_count", v->recycled_contact_count);
  lgen_set_int(L, "distance_iterations", v->distance_iterations);
  lgen_set_int(L, "push_back_iterations", v->push_back_iterations);
  lgen_set_int(L, "root_iterations", v->root_iterations);
  {
    lgen_push_int_table(L, v->color_counts, v->color_counts_count);
    lua_setfield(L, -2, "color_counts");
  }
  {
    lgen_push_int_table(L, v->manifold_counts, v->manifold_counts_count);
    lua_setfield(L, -2, "manifold_counts");
  }
  (void)L;
  (void)v;
}

static void push_LubCounters3d(lua_State *L, const LubCounters3d *v) {
  lua_createtable(L, 0, 20);
  fill_LubCounters3d(L, v);
}

static float tramp_l_phys2d_raycast_all_visitor(void *user,
                                                const LubRayHit *a) {
  LgenCallbacks *cb = (LgenCallbacks *)user;
  lua_State *L = cb->L;
  if (!lgen_callbacks_push(cb, 0))
    return 1.0f;
  push_LubRayHit(L, a);
  if (!lgen_callbacks_call(cb, 0, 1, 1))
    return 1.0f;
  float r = (float)lua_tonumber(L, -1);
  lua_pop(L, 1);
  return r;
}

static bool tramp_l_phys2d_overlap_aabb_visitor(void *user,
                                                const LubShapeView *a) {
  LgenCallbacks *cb = (LgenCallbacks *)user;
  lua_State *L = cb->L;
  if (!lgen_callbacks_push(cb, 0))
    return true;
  push_LubShapeView(L, a);
  if (!lgen_callbacks_call(cb, 0, 1, 1))
    return true;
  bool r = lua_toboolean(L, -1);
  lua_pop(L, 1);
  return r;
}

static float tramp_l_phys2d_shape_cast_all_visitor(void *user,
                                                   const LubRayHit *a) {
  LgenCallbacks *cb = (LgenCallbacks *)user;
  lua_State *L = cb->L;
  if (!lgen_callbacks_push(cb, 0))
    return 1.0f;
  push_LubRayHit(L, a);
  if (!lgen_callbacks_call(cb, 0, 1, 1))
    return 1.0f;
  float r = (float)lua_tonumber(L, -1);
  lua_pop(L, 1);
  return r;
}

static bool tramp_l_phys2d_collide_mover_visitor(void *user,
                                                 const LubMoverPlane *a) {
  LgenCallbacks *cb = (LgenCallbacks *)user;
  lua_State *L = cb->L;
  if (!lgen_callbacks_push(cb, 0))
    return true;
  push_LubMoverPlane(L, a);
  if (!lgen_callbacks_call(cb, 0, 1, 1))
    return true;
  bool r = lua_toboolean(L, -1);
  lua_pop(L, 1);
  return r;
}

static bool tramp_l_phys3d_collide_mover_visitor(void *user,
                                                 const LubMoverPlane3d *a) {
  LgenCallbacks *cb = (LgenCallbacks *)user;
  lua_State *L = cb->L;
  if (!lgen_callbacks_push(cb, 0))
    return true;
  push_LubMoverPlane3d(L, a);
  if (!lgen_callbacks_call(cb, 0, 1, 1))
    return true;
  bool r = lua_toboolean(L, -1);
  lua_pop(L, 1);
  return r;
}

static float tramp_l_phys3d_raycast_all_visitor(void *user,
                                                const LubRayHit3d *a) {
  LgenCallbacks *cb = (LgenCallbacks *)user;
  lua_State *L = cb->L;
  if (!lgen_callbacks_push(cb, 0))
    return 1.0f;
  push_LubRayHit3d(L, a);
  if (!lgen_callbacks_call(cb, 0, 1, 1))
    return 1.0f;
  float r = (float)lua_tonumber(L, -1);
  lua_pop(L, 1);
  return r;
}

static bool tramp_l_phys3d_overlap_aabb_visitor(void *user,
                                                const LubShapeView3d *a) {
  LgenCallbacks *cb = (LgenCallbacks *)user;
  lua_State *L = cb->L;
  if (!lgen_callbacks_push(cb, 0))
    return true;
  push_LubShapeView3d(L, a);
  if (!lgen_callbacks_call(cb, 0, 1, 1))
    return true;
  bool r = lua_toboolean(L, -1);
  lua_pop(L, 1);
  return r;
}

static bool tramp_l_phys3d_overlap_shape_visitor(void *user,
                                                 const LubShapeView3d *a) {
  LgenCallbacks *cb = (LgenCallbacks *)user;
  lua_State *L = cb->L;
  if (!lgen_callbacks_push(cb, 0))
    return true;
  push_LubShapeView3d(L, a);
  if (!lgen_callbacks_call(cb, 0, 1, 1))
    return true;
  bool r = lua_toboolean(L, -1);
  lua_pop(L, 1);
  return r;
}

static float tramp_l_phys3d_shape_cast_all_visitor(void *user,
                                                   const LubRayHit3d *a) {
  LgenCallbacks *cb = (LgenCallbacks *)user;
  lua_State *L = cb->L;
  if (!lgen_callbacks_push(cb, 0))
    return 1.0f;
  push_LubRayHit3d(L, a);
  if (!lgen_callbacks_call(cb, 0, 1, 1))
    return 1.0f;
  float r = (float)lua_tonumber(L, -1);
  lua_pop(L, 1);
  return r;
}

static int l_config(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubConfigOpts opts_v;
  memset(&opts_v, 0, sizeof opts_v);
  const LubConfigOpts *opts = NULL;
  luaL_checktype(L, 1, LUA_TTABLE);
  read_LubConfigOpts(L, 1, &opts_v);
  opts = &opts_v;
  LubStatus st = lub_config(lgen_ctx(), opts);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

static int l_quit(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  lub_quit(lgen_ctx());
  lgen_release(mark);
  return 0;
}

static int l_gfx_begin_pass(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubPassOpts opts_v;
  memset(&opts_v, 0, sizeof opts_v);
  const LubPassOpts *opts = NULL;
  luaL_checktype(L, 1, LUA_TTABLE);
  read_LubPassOpts(L, 1, &opts_v);
  opts = &opts_v;
  LubStatus st = lub_gfx_begin_pass(lgen_ctx(), opts);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

static int l_gfx_end_pass(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStatus st = lub_gfx_end_pass(lgen_ctx());
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

static int l_gfx_use_shader(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr key = lgen_str_arg(L, 1);
  LubStr vs = lgen_str_arg(L, 2);
  LubStr fs = lgen_str_arg(L, 3);
  int32_t version_v = 0;
  const int32_t *version = NULL;
  if (!lua_isnoneornil(L, 4)) {
    version_v = (int32_t)luaL_checkinteger(L, 4);
    version = &version_v;
  }
  LubHandle out = 0;
  LubStatus st = lub_gfx_use_shader(lgen_ctx(), key, vs, fs, version, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (out == 0)
    lua_pushnil(L);
  else
    lgen_push_ref_keyed(L, "shader", out, 0, key);
  return 1;
}

static int l_gfx_use_shader_compute(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr key = lgen_str_arg(L, 1);
  LubStr src = lgen_str_arg(L, 2);
  int32_t version_v = 0;
  const int32_t *version = NULL;
  if (!lua_isnoneornil(L, 3)) {
    version_v = (int32_t)luaL_checkinteger(L, 3);
    version = &version_v;
  }
  LubHandle out = 0;
  LubStatus st =
      lub_gfx_use_shader_compute(lgen_ctx(), key, src, version, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (out == 0)
    lua_pushnil(L);
  else
    lgen_push_ref_keyed(L, "shader", out, 0, key);
  return 1;
}

static int l_gfx_use_buffer(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr key = lgen_str_arg(L, 1);
  int32_t type = (int32_t)luaL_checkinteger(L, 2);
  int32_t data_count = 0;
  const float *data = lgen_floats_arg(L, 3, &data_count, true);
  int32_t version_v = 0;
  const int32_t *version = NULL;
  if (!lua_isnoneornil(L, 4)) {
    version_v = (int32_t)luaL_checkinteger(L, 4);
    version = &version_v;
  }
  LubHandle out = 0;
  LubStatus st = lub_gfx_use_buffer(lgen_ctx(), key, type, data, data_count,
                                    version, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (out == 0)
    lua_pushnil(L);
  else
    lgen_push_ref_keyed(L, "buffer", out, 0, key);
  return 1;
}

static int l_gfx_use_buffer_empty(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr key = lgen_str_arg(L, 1);
  int32_t type = (int32_t)luaL_checkinteger(L, 2);
  int32_t count = (int32_t)luaL_checkinteger(L, 3);
  int32_t version_v = 0;
  const int32_t *version = NULL;
  if (!lua_isnoneornil(L, 4)) {
    version_v = (int32_t)luaL_checkinteger(L, 4);
    version = &version_v;
  }
  LubHandle out = 0;
  LubStatus st =
      lub_gfx_use_buffer_empty(lgen_ctx(), key, type, count, version, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (out == 0)
    lua_pushnil(L);
  else
    lgen_push_ref_keyed(L, "buffer", out, 0, key);
  return 1;
}

static int l_gfx_use_texture(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr key = lgen_str_arg(L, 1);
  int32_t w = (int32_t)luaL_checkinteger(L, 2);
  int32_t h = (int32_t)luaL_checkinteger(L, 3);
  int32_t fmt = (int32_t)luaL_checkinteger(L, 4);
  int32_t px_count = 0;
  const int32_t *px = lgen_ints_arg(L, 5, &px_count, false);
  int32_t version_v = 0;
  const int32_t *version = NULL;
  if (!lua_isnoneornil(L, 6)) {
    version_v = (int32_t)luaL_checkinteger(L, 6);
    version = &version_v;
  }
  LubTextureOpts opts_v;
  memset(&opts_v, 0, sizeof opts_v);
  const LubTextureOpts *opts = NULL;
  if (!lua_isnoneornil(L, 7)) {
    luaL_checktype(L, 7, LUA_TTABLE);
    read_LubTextureOpts(L, 7, &opts_v);
    opts = &opts_v;
  }
  LubHandle out = 0;
  LubStatus st = lub_gfx_use_texture(lgen_ctx(), key, w, h, fmt, px, px_count,
                                     version, opts, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (out == 0)
    lua_pushnil(L);
  else
    lgen_push_ref_keyed(L, "texture", out, 0, key);
  return 1;
}

static int l_gfx_use_texture_bytes(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr key = lgen_str_arg(L, 1);
  int32_t w = (int32_t)luaL_checkinteger(L, 2);
  int32_t h = (int32_t)luaL_checkinteger(L, 3);
  int32_t fmt = (int32_t)luaL_checkinteger(L, 4);
  int32_t px_len = 0;
  const uint8_t *px = lgen_bytes_arg(L, 5, &px_len, false);
  int32_t version_v = 0;
  const int32_t *version = NULL;
  if (!lua_isnoneornil(L, 6)) {
    version_v = (int32_t)luaL_checkinteger(L, 6);
    version = &version_v;
  }
  LubTextureOpts opts_v;
  memset(&opts_v, 0, sizeof opts_v);
  const LubTextureOpts *opts = NULL;
  if (!lua_isnoneornil(L, 7)) {
    luaL_checktype(L, 7, LUA_TTABLE);
    read_LubTextureOpts(L, 7, &opts_v);
    opts = &opts_v;
  }
  LubHandle out = 0;
  LubStatus st = lub_gfx_use_texture_bytes(lgen_ctx(), key, w, h, fmt, px,
                                           px_len, version, opts, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (out == 0)
    lua_pushnil(L);
  else
    lgen_push_ref_keyed(L, "texture", out, 0, key);
  return 1;
}

static int l_gfx_lookup_texture(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr key = lgen_str_arg(L, 1);
  LubHandle out = lub_gfx_lookup_texture(lgen_ctx(), key);
  lgen_release(mark);
  if (out == 0)
    lua_pushnil(L);
  else
    lgen_push_ref_keyed(L, "texture", out, 0, key);
  return 1;
}

static int l_gfx_lookup_shader(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr key = lgen_str_arg(L, 1);
  LubHandle out = lub_gfx_lookup_shader(lgen_ctx(), key);
  lgen_release(mark);
  if (out == 0)
    lua_pushnil(L);
  else
    lgen_push_ref_keyed(L, "shader", out, 0, key);
  return 1;
}

static int l_gfx_lookup_buffer(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr key = lgen_str_arg(L, 1);
  LubHandle out = lub_gfx_lookup_buffer(lgen_ctx(), key);
  lgen_release(mark);
  if (out == 0)
    lua_pushnil(L);
  else
    lgen_push_ref_keyed(L, "buffer", out, 0, key);
  return 1;
}

static int l_gfx_resource_info(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  int32_t handle = (int32_t)luaL_checkinteger(L, 1);
  LubStr key = {NULL, 0};
  int32_t version = 0;
  bool out = lub_gfx_resource_info(lgen_ctx(), handle, &key, &version);
  lgen_release(mark);
  lua_pushboolean(L, out);
  if (key.len == 0 && !key.ptr)
    lua_pushnil(L);
  else
    lgen_push_str(L, key);
  lua_pushinteger(L, version);
  return 3;
}

static int l_gfx_read_texture(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr rb = lgen_keyed_arg(L, 1, "readback");
  LubHandle tex = lgen_ref_arg(L, 2, "texture", true);
  int32_t id_v = 0;
  const int32_t *id = NULL;
  if (!lua_isnoneornil(L, 3)) {
    id_v = (int32_t)luaL_checkinteger(L, 3);
    id = &id_v;
  }
  int32_t status = 0;
  LubView bytes = {NULL, 0, 0};
  int32_t width = 0;
  int32_t height = 0;
  int32_t format = 0;
  int32_t stride = 0;
  int32_t result_id = 0;
  int32_t dropped = 0;
  LubStr error = {NULL, 0};
  LubStatus st = lub_gfx_read_texture(lgen_ctx(), rb, tex, id, &status, &bytes,
                                      &width, &height, &format, &stride,
                                      &result_id, &dropped, &error);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushnil(L);
    lua_pushnil(L);
    lua_pushnil(L);
    lua_pushnil(L);
    lua_pushnil(L);
    lua_pushnil(L);
    lua_pushnil(L);
    lua_pushnil(L);
    return 9;
  }
  lua_pushstring(L, name_LubGfxReadbackStatus(status)
                        ? name_LubGfxReadbackStatus(status)
                        : "");
  if (!bytes.ptr)
    lua_pushnil(L);
  else
    lgen_push_bytes_view(L, bytes);
  lua_pushinteger(L, width);
  lua_pushinteger(L, height);
  lua_pushinteger(L, format);
  lua_pushinteger(L, stride);
  lua_pushinteger(L, result_id);
  lua_pushinteger(L, dropped);
  if (error.len == 0 && !error.ptr)
    lua_pushnil(L);
  else
    lgen_push_str(L, error);
  return 9;
}

static int l_gfx_draw(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  int32_t count = (int32_t)luaL_checkinteger(L, 1);
  int32_t bindings_count = 0;
  const LubBinding *bindings = lgen_bindings_arg(L, 2, &bindings_count);
  LubDrawOpts opts_v;
  memset(&opts_v, 0, sizeof opts_v);
  const LubDrawOpts *opts = NULL;
  luaL_checktype(L, 3, LUA_TTABLE);
  read_LubDrawOpts(L, 3, &opts_v);
  opts = &opts_v;
  LubStatus st =
      lub_gfx_draw(lgen_ctx(), count, bindings, bindings_count, opts);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

static int l_gfx_dispatch(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  int32_t x = (int32_t)luaL_checkinteger(L, 1);
  int32_t y = (int32_t)luaL_checkinteger(L, 2);
  int32_t z = (int32_t)luaL_checkinteger(L, 3);
  int32_t bindings_count = 0;
  const LubBinding *bindings = lgen_bindings_arg(L, 4, &bindings_count);
  LubDispatchOpts opts_v;
  memset(&opts_v, 0, sizeof opts_v);
  const LubDispatchOpts *opts = NULL;
  luaL_checktype(L, 5, LUA_TTABLE);
  read_LubDispatchOpts(L, 5, &opts_v);
  opts = &opts_v;
  LubStatus st =
      lub_gfx_dispatch(lgen_ctx(), x, y, z, bindings, bindings_count, opts);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

static int l_gfx_size(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  int32_t w = 0;
  int32_t h = 0;
  lub_gfx_size(lgen_ctx(), &w, &h);
  lgen_release(mark);
  lua_pushinteger(L, w);
  lua_pushinteger(L, h);
  return 2;
}

static int l_input_key_down(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr key = lgen_str_arg(L, 1);
  bool out = lub_input_key_down(lgen_ctx(), key);
  lgen_release(mark);
  lua_pushboolean(L, out);
  return 1;
}

static int l_input_key_pressed(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr key = lgen_str_arg(L, 1);
  bool out = lub_input_key_pressed(lgen_ctx(), key);
  lgen_release(mark);
  lua_pushboolean(L, out);
  return 1;
}

static int l_input_key_released(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr key = lgen_str_arg(L, 1);
  bool out = lub_input_key_released(lgen_ctx(), key);
  lgen_release(mark);
  lua_pushboolean(L, out);
  return 1;
}

static int l_input_mouse_down(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  int32_t button_v = 0;
  const int32_t *button = NULL;
  if (!lua_isnoneornil(L, 1)) {
    button_v = (int32_t)luaL_checkinteger(L, 1);
    button = &button_v;
  }
  bool out = lub_input_mouse_down(lgen_ctx(), button);
  lgen_release(mark);
  lua_pushboolean(L, out);
  return 1;
}

static int l_input_mouse_pressed(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  int32_t button_v = 0;
  const int32_t *button = NULL;
  if (!lua_isnoneornil(L, 1)) {
    button_v = (int32_t)luaL_checkinteger(L, 1);
    button = &button_v;
  }
  bool out = lub_input_mouse_pressed(lgen_ctx(), button);
  lgen_release(mark);
  lua_pushboolean(L, out);
  return 1;
}

static int l_input_mouse_released(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  int32_t button_v = 0;
  const int32_t *button = NULL;
  if (!lua_isnoneornil(L, 1)) {
    button_v = (int32_t)luaL_checkinteger(L, 1);
    button = &button_v;
  }
  bool out = lub_input_mouse_released(lgen_ctx(), button);
  lgen_release(mark);
  lua_pushboolean(L, out);
  return 1;
}

static int l_input_mouse_pos(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  float x = 0;
  float y = 0;
  lub_input_mouse_pos(lgen_ctx(), &x, &y);
  lgen_release(mark);
  lua_pushnumber(L, x);
  lua_pushnumber(L, y);
  return 2;
}

static int l_input_mouse_delta(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  float dx = 0;
  float dy = 0;
  lub_input_mouse_delta(lgen_ctx(), &dx, &dy);
  lgen_release(mark);
  lua_pushnumber(L, dx);
  lua_pushnumber(L, dy);
  return 2;
}

static int l_io_load_text(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr path = lgen_str_arg(L, 1);
  LubStr text = {NULL, 0};
  int32_t version = 0;
  int32_t status = 0;
  LubStr error = {NULL, 0};
  LubStatus st =
      lub_io_load_text(lgen_ctx(), path, &text, &version, &status, &error);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushnil(L);
    lua_pushnil(L);
    lua_pushnil(L);
    return 4;
  }
  if (text.len == 0 && !text.ptr)
    lua_pushnil(L);
  else
    lgen_push_str(L, text);
  lua_pushinteger(L, version);
  lua_pushstring(L, name_LubIoStatus(status) ? name_LubIoStatus(status) : "");
  if (error.len == 0 && !error.ptr)
    lua_pushnil(L);
  else
    lgen_push_str(L, error);
  return 4;
}

static int l_io_load_floats(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr path = lgen_str_arg(L, 1);
  const float *data = NULL;
  int32_t data_count = 0;
  int32_t version = 0;
  int32_t status = 0;
  LubStr error = {NULL, 0};
  LubStatus st = lub_io_load_floats(lgen_ctx(), path, &data, &data_count,
                                    &version, &status, &error);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushnil(L);
    lua_pushnil(L);
    lua_pushnil(L);
    return 4;
  }
  if (!data)
    lua_pushnil(L);
  else
    lgen_push_float_view(L, data, data_count);
  lua_pushinteger(L, version);
  lua_pushstring(L, name_LubIoStatus(status) ? name_LubIoStatus(status) : "");
  if (error.len == 0 && !error.ptr)
    lua_pushnil(L);
  else
    lgen_push_str(L, error);
  return 4;
}

static int l_io_load_gltf(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr path = lgen_str_arg(L, 1);
  LubGltfMesh mesh;
  memset(&mesh, 0, sizeof mesh);
  bool has_mesh = false;
  int32_t version = 0;
  int32_t status = 0;
  LubStr error = {NULL, 0};
  LubStatus st = lub_io_load_gltf(lgen_ctx(), path, &mesh, &has_mesh, &version,
                                  &status, &error);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushnil(L);
    lua_pushnil(L);
    lua_pushnil(L);
    return 4;
  }
  if (!has_mesh)
    lua_pushnil(L);
  else
    push_LubGltfMesh(L, &mesh);
  lua_pushinteger(L, version);
  lua_pushstring(L, name_LubIoStatus(status) ? name_LubIoStatus(status) : "");
  if (error.len == 0 && !error.ptr)
    lua_pushnil(L);
  else
    lgen_push_str(L, error);
  return 4;
}

static int l_io_interleave_pn(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubMeshData mesh_v;
  memset(&mesh_v, 0, sizeof mesh_v);
  const LubMeshData *mesh = NULL;
  luaL_checktype(L, 1, LUA_TTABLE);
  read_LubMeshData(L, 1, &mesh_v);
  mesh = &mesh_v;
  const float *out = NULL;
  int32_t out_count = 0;
  LubStatus st = lub_io_interleave_pn(lgen_ctx(), mesh, &out, &out_count);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  lgen_push_float_view(L, out, out_count);
  return 1;
}

static int l_io_interleave_pncm(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubMeshData mesh_v;
  memset(&mesh_v, 0, sizeof mesh_v);
  const LubMeshData *mesh = NULL;
  luaL_checktype(L, 1, LUA_TTABLE);
  read_LubMeshData(L, 1, &mesh_v);
  mesh = &mesh_v;
  const float *out = NULL;
  int32_t out_count = 0;
  LubStatus st = lub_io_interleave_pncm(lgen_ctx(), mesh, &out, &out_count);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  lgen_push_float_view(L, out, out_count);
  return 1;
}

static int l_io_interleave_pncmw(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubMeshData mesh_v;
  memset(&mesh_v, 0, sizeof mesh_v);
  const LubMeshData *mesh = NULL;
  luaL_checktype(L, 1, LUA_TTABLE);
  read_LubMeshData(L, 1, &mesh_v);
  mesh = &mesh_v;
  const float *out = NULL;
  int32_t out_count = 0;
  LubStatus st = lub_io_interleave_pncmw(lgen_ctx(), mesh, &out, &out_count);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  lgen_push_float_view(L, out, out_count);
  return 1;
}

static int l_io_interleave_pnu(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubMeshData mesh_v;
  memset(&mesh_v, 0, sizeof mesh_v);
  const LubMeshData *mesh = NULL;
  luaL_checktype(L, 1, LUA_TTABLE);
  read_LubMeshData(L, 1, &mesh_v);
  mesh = &mesh_v;
  const float *out = NULL;
  int32_t out_count = 0;
  LubStatus st = lub_io_interleave_pnu(lgen_ctx(), mesh, &out, &out_count);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  lgen_push_float_view(L, out, out_count);
  return 1;
}

static int l_io_interleave_pnut(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubMeshData mesh_v;
  memset(&mesh_v, 0, sizeof mesh_v);
  const LubMeshData *mesh = NULL;
  luaL_checktype(L, 1, LUA_TTABLE);
  read_LubMeshData(L, 1, &mesh_v);
  mesh = &mesh_v;
  const float *out = NULL;
  int32_t out_count = 0;
  LubStatus st = lub_io_interleave_pnut(lgen_ctx(), mesh, &out, &out_count);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  lgen_push_float_view(L, out, out_count);
  return 1;
}

static int l_mesh_surface_nets(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  int32_t grid_count = 0;
  const float *grid = lgen_floats_arg(L, 1, &grid_count, true);
  int32_t nx = (int32_t)luaL_checkinteger(L, 2);
  int32_t ny = (int32_t)luaL_checkinteger(L, 3);
  int32_t nz = (int32_t)luaL_checkinteger(L, 4);
  float cell_v = 0;
  const float *cell = NULL;
  if (!lua_isnoneornil(L, 5)) {
    cell_v = (float)luaL_checknumber(L, 5);
    cell = &cell_v;
  }
  float ox_v = 0;
  const float *ox = NULL;
  if (!lua_isnoneornil(L, 6)) {
    ox_v = (float)luaL_checknumber(L, 6);
    ox = &ox_v;
  }
  float oy_v = 0;
  const float *oy = NULL;
  if (!lua_isnoneornil(L, 7)) {
    oy_v = (float)luaL_checknumber(L, 7);
    oy = &oy_v;
  }
  float oz_v = 0;
  const float *oz = NULL;
  if (!lua_isnoneornil(L, 8)) {
    oz_v = (float)luaL_checknumber(L, 8);
    oz = &oz_v;
  }
  LubMeshData out;
  memset(&out, 0, sizeof out);
  LubStatus st = lub_mesh_surface_nets(lgen_ctx(), grid, grid_count, nx, ny, nz,
                                       cell, ox, oy, oz, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_LubMeshData(L, &out);
  return 1;
}

static int l_mesh_sdf_mesh(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  int32_t nodes_count = 0;
  const LubSdfNodeDesc *nodes = (const LubSdfNodeDesc *)lgen_records_arg(
      L, 1, sizeof(LubSdfNodeDesc), read_LubSdfNodeDesc, &nodes_count, true);
  int32_t root = (int32_t)luaL_checkinteger(L, 2);
  int32_t n = (int32_t)luaL_checkinteger(L, 3);
  float skin_k_v = 0;
  const float *skin_k = NULL;
  if (!lua_isnoneornil(L, 4)) {
    skin_k_v = (float)luaL_checknumber(L, 4);
    skin_k = &skin_k_v;
  }
  LubMeshData out;
  memset(&out, 0, sizeof out);
  LubStatus st =
      lub_mesh_sdf_mesh(lgen_ctx(), nodes, nodes_count, root, n, skin_k, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_LubMeshData(L, &out);
  return 1;
}

static int l_font_metrics(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr ttf = lgen_str_arg(L, 1);
  LubFontMetrics out;
  memset(&out, 0, sizeof out);
  LubStatus st = lub_font_metrics(lgen_ctx(), ttf, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_LubFontMetrics(L, &out);
  return 1;
}

static int l_font_glyph(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr ttf = lgen_str_arg(L, 1);
  int32_t codepoint = (int32_t)luaL_checkinteger(L, 2);
  float px = (float)luaL_checknumber(L, 3);
  LubGlyphBitmap out;
  memset(&out, 0, sizeof out);
  bool has = false;
  LubStatus st = lub_font_glyph(lgen_ctx(), ttf, codepoint, px, &out, &has);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (!has)
    lua_pushnil(L);
  else
    push_LubGlyphBitmap(L, &out);
  return 1;
}

static int l_font_glyph_mesh(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr ttf = lgen_str_arg(L, 1);
  int32_t codepoint = (int32_t)luaL_checkinteger(L, 2);
  float tolerance_v = 0;
  const float *tolerance = NULL;
  if (!lua_isnoneornil(L, 3)) {
    tolerance_v = (float)luaL_checknumber(L, 3);
    tolerance = &tolerance_v;
  }
  LubGlyphMesh out;
  memset(&out, 0, sizeof out);
  bool has = false;
  LubStatus st =
      lub_font_glyph_mesh(lgen_ctx(), ttf, codepoint, tolerance, &out, &has);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (!has)
    lua_pushnil(L);
  else
    push_LubGlyphMesh(L, &out);
  return 1;
}

static int l_font_kern(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr ttf = lgen_str_arg(L, 1);
  int32_t cp1 = (int32_t)luaL_checkinteger(L, 2);
  int32_t cp2 = (int32_t)luaL_checkinteger(L, 3);
  float out = 0;
  LubStatus st = lub_font_kern(lgen_ctx(), ttf, cp1, cp2, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  lua_pushnumber(L, out);
  return 1;
}

static int l_ui_render(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStatus st = lub_ui_render(lgen_ctx());
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

static int l_ui_begin_window(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr title = lgen_str_arg(L, 1);
  bool out = lub_ui_begin_window(lgen_ctx(), title);
  lgen_release(mark);
  lua_pushboolean(L, out);
  return 1;
}

static int l_ui_end_window(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  lub_ui_end_window(lgen_ctx());
  lgen_release(mark);
  return 0;
}

static int l_ui_text(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr s = lgen_str_arg(L, 1);
  lub_ui_text(lgen_ctx(), s);
  lgen_release(mark);
  return 0;
}

static int l_ui_button(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr label = lgen_str_arg(L, 1);
  bool out = lub_ui_button(lgen_ctx(), label);
  lgen_release(mark);
  lua_pushboolean(L, out);
  return 1;
}

static int l_ui_checkbox(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr label = lgen_str_arg(L, 1);
  bool v = lua_toboolean(L, 2);
  bool out = lub_ui_checkbox(lgen_ctx(), label, v);
  lgen_release(mark);
  lua_pushboolean(L, out);
  return 1;
}

static int l_ui_slider_float(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr label = lgen_str_arg(L, 1);
  float v = (float)luaL_checknumber(L, 2);
  float min = (float)luaL_checknumber(L, 3);
  float max = (float)luaL_checknumber(L, 4);
  float out = lub_ui_slider_float(lgen_ctx(), label, v, min, max);
  lgen_release(mark);
  lua_pushnumber(L, out);
  return 1;
}

static int l_ui_slider_int(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr label = lgen_str_arg(L, 1);
  int32_t v = (int32_t)luaL_checkinteger(L, 2);
  int32_t min = (int32_t)luaL_checkinteger(L, 3);
  int32_t max = (int32_t)luaL_checkinteger(L, 4);
  int32_t out = lub_ui_slider_int(lgen_ctx(), label, v, min, max);
  lgen_release(mark);
  lua_pushinteger(L, out);
  return 1;
}

static int l_ui_drag_float(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr label = lgen_str_arg(L, 1);
  float v = (float)luaL_checknumber(L, 2);
  float speed_v = 0;
  const float *speed = NULL;
  if (!lua_isnoneornil(L, 3)) {
    speed_v = (float)luaL_checknumber(L, 3);
    speed = &speed_v;
  }
  float min_v = 0;
  const float *min = NULL;
  if (!lua_isnoneornil(L, 4)) {
    min_v = (float)luaL_checknumber(L, 4);
    min = &min_v;
  }
  float max_v = 0;
  const float *max = NULL;
  if (!lua_isnoneornil(L, 5)) {
    max_v = (float)luaL_checknumber(L, 5);
    max = &max_v;
  }
  float out = lub_ui_drag_float(lgen_ctx(), label, v, speed, min, max);
  lgen_release(mark);
  lua_pushnumber(L, out);
  return 1;
}

static int l_ui_color_edit3(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr label = lgen_str_arg(L, 1);
  float r = (float)luaL_checknumber(L, 2);
  float g = (float)luaL_checknumber(L, 3);
  float b = (float)luaL_checknumber(L, 4);
  float new_r = 0;
  float new_g = 0;
  float new_b = 0;
  lub_ui_color_edit3(lgen_ctx(), label, r, g, b, &new_r, &new_g, &new_b);
  lgen_release(mark);
  lua_pushnumber(L, new_r);
  lua_pushnumber(L, new_g);
  lua_pushnumber(L, new_b);
  return 3;
}

static int l_ui_separator(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  lub_ui_separator(lgen_ctx());
  lgen_release(mark);
  return 0;
}

static int l_ui_same_line(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  lub_ui_same_line(lgen_ctx());
  lgen_release(mark);
  return 0;
}

static int l_ui_tree_node(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr label = lgen_str_arg(L, 1);
  bool default_open_v = 0;
  const bool *default_open = NULL;
  if (!lua_isnoneornil(L, 2)) {
    default_open_v = lua_toboolean(L, 2);
    default_open = &default_open_v;
  }
  bool out = lub_ui_tree_node(lgen_ctx(), label, default_open);
  lgen_release(mark);
  lua_pushboolean(L, out);
  return 1;
}

static int l_ui_tree_pop(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  lub_ui_tree_pop(lgen_ctx());
  lgen_release(mark);
  return 0;
}

static int l_ui_set_next_window(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  float x = (float)luaL_checknumber(L, 1);
  float y = (float)luaL_checknumber(L, 2);
  float w = (float)luaL_checknumber(L, 3);
  float h = (float)luaL_checknumber(L, 4);
  lub_ui_set_next_window(lgen_ctx(), x, y, w, h);
  lgen_release(mark);
  return 0;
}

static int l_ui_want_capture_mouse(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  bool out = lub_ui_want_capture_mouse(lgen_ctx());
  lgen_release(mark);
  lua_pushboolean(L, out);
  return 1;
}

static int l_host_available(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  bool out = lub_host_available(lgen_ctx());
  lgen_release(mark);
  lua_pushboolean(L, out);
  return 1;
}

static int l_host_send(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr topic = lgen_str_arg(L, 1);
  LubStr payload = lgen_str_arg(L, 2);
  lub_host_send(lgen_ctx(), topic, payload);
  lgen_release(mark);
  return 0;
}

static int l_host_poll(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr topic = {NULL, 0};
  LubStr payload = {NULL, 0};
  LubStatus st = lub_host_poll(lgen_ctx(), &topic, &payload);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushnil(L);
    return 2;
  }
  if (topic.len == 0 && !topic.ptr)
    lua_pushnil(L);
  else
    lgen_push_str(L, topic);
  if (payload.len == 0 && !payload.ptr)
    lua_pushnil(L);
  else
    lgen_push_str(L, payload);
  return 2;
}

static int l_audio_snd(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr key = lgen_str_arg(L, 1);
  int32_t data_count = 0;
  const float *data = lgen_floats_arg(L, 2, &data_count, true);
  int32_t channels = (int32_t)luaL_checkinteger(L, 3);
  int32_t rate = (int32_t)luaL_checkinteger(L, 4);
  int32_t version_v = 0;
  const int32_t *version = NULL;
  if (!lua_isnoneornil(L, 5)) {
    version_v = (int32_t)luaL_checkinteger(L, 5);
    version = &version_v;
  }
  int32_t out = 0;
  LubStatus st = lub_audio_snd(lgen_ctx(), key, data, data_count, channels,
                               rate, version, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  lua_pushinteger(L, out);
  return 1;
}

static int l_audio_snd_bytes(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr key = lgen_str_arg(L, 1);
  int32_t data_len = 0;
  const uint8_t *data = lgen_bytes_arg(L, 2, &data_len, true);
  int32_t channels = (int32_t)luaL_checkinteger(L, 3);
  int32_t rate = (int32_t)luaL_checkinteger(L, 4);
  int32_t version_v = 0;
  const int32_t *version = NULL;
  if (!lua_isnoneornil(L, 5)) {
    version_v = (int32_t)luaL_checkinteger(L, 5);
    version = &version_v;
  }
  int32_t out = 0;
  LubStatus st = lub_audio_snd_bytes(lgen_ctx(), key, data, data_len, channels,
                                     rate, version, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  lua_pushinteger(L, out);
  return 1;
}

static int l_audio_decode(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  int32_t data_len = 0;
  const uint8_t *data = lgen_bytes_arg(L, 1, &data_len, true);
  LubView bytes = {NULL, 0, 0};
  int32_t channels = 0;
  int32_t rate = 0;
  LubStatus st =
      lub_audio_decode(lgen_ctx(), data, data_len, &bytes, &channels, &rate);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushnil(L);
    lua_pushnil(L);
    return 3;
  }
  if (!bytes.ptr)
    lua_pushnil(L);
  else
    lgen_push_bytes_view(L, bytes);
  lua_pushinteger(L, channels);
  lua_pushinteger(L, rate);
  return 3;
}

static int l_audio_play(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  int32_t snd = (int32_t)luaL_checkinteger(L, 1);
  LubPlayOpts opts_v;
  memset(&opts_v, 0, sizeof opts_v);
  const LubPlayOpts *opts = NULL;
  if (!lua_isnoneornil(L, 2)) {
    luaL_checktype(L, 2, LUA_TTABLE);
    read_LubPlayOpts(L, 2, &opts_v);
    opts = &opts_v;
  }
  bool out = lub_audio_play(lgen_ctx(), snd, opts);
  lgen_release(mark);
  lua_pushboolean(L, out);
  return 1;
}

static int l_audio_voice(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr key = lgen_str_arg(L, 1);
  int32_t snd = (int32_t)luaL_checkinteger(L, 2);
  LubVoiceOpts opts_v;
  memset(&opts_v, 0, sizeof opts_v);
  const LubVoiceOpts *opts = NULL;
  if (!lua_isnoneornil(L, 3)) {
    luaL_checktype(L, 3, LUA_TTABLE);
    read_LubVoiceOpts(L, 3, &opts_v);
    opts = &opts_v;
  }
  bool out = lub_audio_voice(lgen_ctx(), key, snd, opts);
  lgen_release(mark);
  lua_pushboolean(L, out);
  return 1;
}

static int l_audio_master_volume(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  float volume = (float)luaL_checknumber(L, 1);
  lub_audio_master_volume(lgen_ctx(), volume);
  lgen_release(mark);
  return 0;
}

static int l_audio_info(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubAudioInfo out;
  memset(&out, 0, sizeof out);
  lub_audio_info(lgen_ctx(), &out);
  lgen_release(mark);
  push_LubAudioInfo(L, &out);
  return 1;
}

static int l_sys_is_web(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  bool out = lub_sys_is_web(lgen_ctx());
  lgen_release(mark);
  lua_pushboolean(L, out);
  return 1;
}

static int l_sys_fnv1a64(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr s = lgen_str_arg(L, 1);
  int32_t out = lub_sys_fnv1a64(lgen_ctx(), s);
  lgen_release(mark);
  lua_pushinteger(L, out);
  return 1;
}

static int l_sys_actual_fps(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  float out = lub_sys_actual_fps(lgen_ctx());
  lgen_release(mark);
  lua_pushnumber(L, out);
  return 1;
}

static int l_profiler_enabled(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  bool out = lub_profiler_enabled(lgen_ctx());
  lgen_release(mark);
  lua_pushboolean(L, out);
  return 1;
}

static int l_profiler_begin_scope(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr name = lgen_str_arg(L, 1);
  lub_profiler_begin_scope(lgen_ctx(), name);
  lgen_release(mark);
  return 0;
}

static int l_profiler_end_scope(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr name = lgen_str_arg(L, 1);
  lub_profiler_end_scope(lgen_ctx(), name);
  lgen_release(mark);
  return 0;
}

static int l_profiler_reset(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  lub_profiler_reset(lgen_ctx());
  lgen_release(mark);
  return 0;
}

static int l_profiler_report(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr label = lgen_str_arg(L, 1);
  lub_profiler_report(lgen_ctx(), label);
  lgen_release(mark);
  return 0;
}

static int l_phys2d_find_world(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr key = lgen_str_arg(L, 1);
  LubHandle out = lub_phys2d_find_world(lgen_ctx(), key);
  lgen_release(mark);
  if (out == 0)
    lua_pushnil(L);
  else
    lgen_push_ref_keyed(L, "world", out, 0, key);
  return 1;
}

static int l_phys2d_find_body(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world", true);
  LubStr key = lgen_str_arg(L, 2);
  LubHandle out = lub_phys2d_find_body(lgen_ctx(), world, key);
  lgen_release(mark);
  if (out == 0)
    lua_pushnil(L);
  else
    lgen_push_ref_keyed(L, "body", out, 1, key);
  return 1;
}

static int l_phys2d_find_shape(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body", true);
  LubStr key = lgen_str_arg(L, 2);
  LubHandle out = lub_phys2d_find_shape(lgen_ctx(), body, key);
  lgen_release(mark);
  if (out == 0)
    lua_pushnil(L);
  else
    lgen_push_ref_keyed(L, "shape", out, 1, key);
  return 1;
}

static int l_phys2d_find_chain(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body", true);
  LubStr key = lgen_str_arg(L, 2);
  LubHandle out = lub_phys2d_find_chain(lgen_ctx(), body, key);
  lgen_release(mark);
  if (out == 0)
    lua_pushnil(L);
  else
    lgen_push_ref_keyed(L, "chain", out, 1, key);
  return 1;
}

static int l_phys2d_find_joint(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world", true);
  LubStr key = lgen_str_arg(L, 2);
  LubHandle out = lub_phys2d_find_joint(lgen_ctx(), world, key);
  lgen_release(mark);
  if (out == 0)
    lua_pushnil(L);
  else
    lgen_push_ref_keyed(L, "joint", out, 1, key);
  return 1;
}

static int l_phys2d_world(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr key = lgen_str_arg(L, 1);
  LubWorldOpts opts_v;
  memset(&opts_v, 0, sizeof opts_v);
  const LubWorldOpts *opts = NULL;
  if (!lua_isnoneornil(L, 2)) {
    luaL_checktype(L, 2, LUA_TTABLE);
    read_LubWorldOpts(L, 2, &opts_v);
    opts = &opts_v;
  }
  LubHandle out = 0;
  LubStatus st = lub_phys2d_world(lgen_ctx(), key, opts, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (out == 0)
    lua_pushnil(L);
  else
    lgen_push_ref_keyed(L, "world", out, 0, key);
  return 1;
}

static int l_phys2d_begin(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world", true);
  LubBeginOpts opts_v;
  memset(&opts_v, 0, sizeof opts_v);
  const LubBeginOpts *opts = NULL;
  if (!lua_isnoneornil(L, 2)) {
    luaL_checktype(L, 2, LUA_TTABLE);
    read_LubBeginOpts(L, 2, &opts_v);
    opts = &opts_v;
  }
  LubStatus st = lub_phys2d_begin(lgen_ctx(), world, opts);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

static int l_phys2d_world_info(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world", true);
  LubWorldInfo out;
  memset(&out, 0, sizeof out);
  LubStatus st = lub_phys2d_world_info(lgen_ctx(), world, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_LubWorldInfo(L, &out);
  return 1;
}

static int l_phys2d_body(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world", true);
  LubStr key = lgen_str_arg(L, 2);
  LubBodyDesc desc_v;
  memset(&desc_v, 0, sizeof desc_v);
  const LubBodyDesc *desc = NULL;
  luaL_checktype(L, 3, LUA_TTABLE);
  read_LubBodyDesc(L, 3, &desc_v);
  desc = &desc_v;
  LubHandle out = 0;
  LubStatus st = lub_phys2d_body(lgen_ctx(), world, key, desc, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (out == 0)
    lua_pushnil(L);
  else
    lgen_push_ref_keyed(L, "body", out, 1, key);
  return 1;
}

static int l_phys2d_box(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body", true);
  LubStr key = lgen_str_arg(L, 2);
  LubBoxDesc desc_v;
  memset(&desc_v, 0, sizeof desc_v);
  const LubBoxDesc *desc = NULL;
  luaL_checktype(L, 3, LUA_TTABLE);
  read_LubBoxDesc(L, 3, &desc_v);
  desc = &desc_v;
  LubHandle out = 0;
  LubStatus st = lub_phys2d_box(lgen_ctx(), body, key, desc, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (out == 0)
    lua_pushnil(L);
  else
    lgen_push_ref_keyed(L, "shape", out, 1, key);
  return 1;
}

static int l_phys2d_circle(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body", true);
  LubStr key = lgen_str_arg(L, 2);
  LubCircleDesc desc_v;
  memset(&desc_v, 0, sizeof desc_v);
  const LubCircleDesc *desc = NULL;
  luaL_checktype(L, 3, LUA_TTABLE);
  read_LubCircleDesc(L, 3, &desc_v);
  desc = &desc_v;
  LubHandle out = 0;
  LubStatus st = lub_phys2d_circle(lgen_ctx(), body, key, desc, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (out == 0)
    lua_pushnil(L);
  else
    lgen_push_ref_keyed(L, "shape", out, 1, key);
  return 1;
}

static int l_phys2d_capsule(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body", true);
  LubStr key = lgen_str_arg(L, 2);
  LubCapsuleDesc desc_v;
  memset(&desc_v, 0, sizeof desc_v);
  const LubCapsuleDesc *desc = NULL;
  luaL_checktype(L, 3, LUA_TTABLE);
  read_LubCapsuleDesc(L, 3, &desc_v);
  desc = &desc_v;
  LubHandle out = 0;
  LubStatus st = lub_phys2d_capsule(lgen_ctx(), body, key, desc, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (out == 0)
    lua_pushnil(L);
  else
    lgen_push_ref_keyed(L, "shape", out, 1, key);
  return 1;
}

static int l_phys2d_segment(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body", true);
  LubStr key = lgen_str_arg(L, 2);
  LubSegmentDesc desc_v;
  memset(&desc_v, 0, sizeof desc_v);
  const LubSegmentDesc *desc = NULL;
  luaL_checktype(L, 3, LUA_TTABLE);
  read_LubSegmentDesc(L, 3, &desc_v);
  desc = &desc_v;
  LubHandle out = 0;
  LubStatus st = lub_phys2d_segment(lgen_ctx(), body, key, desc, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (out == 0)
    lua_pushnil(L);
  else
    lgen_push_ref_keyed(L, "shape", out, 1, key);
  return 1;
}

static int l_phys2d_polygon(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body", true);
  LubStr key = lgen_str_arg(L, 2);
  LubPolygonDesc desc_v;
  memset(&desc_v, 0, sizeof desc_v);
  const LubPolygonDesc *desc = NULL;
  luaL_checktype(L, 3, LUA_TTABLE);
  read_LubPolygonDesc(L, 3, &desc_v);
  desc = &desc_v;
  LubHandle out = 0;
  LubStatus st = lub_phys2d_polygon(lgen_ctx(), body, key, desc, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (out == 0)
    lua_pushnil(L);
  else
    lgen_push_ref_keyed(L, "shape", out, 1, key);
  return 1;
}

static int l_phys2d_chain(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body", true);
  LubStr key = lgen_str_arg(L, 2);
  LubChainDesc desc_v;
  memset(&desc_v, 0, sizeof desc_v);
  const LubChainDesc *desc = NULL;
  luaL_checktype(L, 3, LUA_TTABLE);
  read_LubChainDesc(L, 3, &desc_v);
  desc = &desc_v;
  LubHandle out = 0;
  LubStatus st = lub_phys2d_chain(lgen_ctx(), body, key, desc, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (out == 0)
    lua_pushnil(L);
  else
    lgen_push_ref_keyed(L, "chain", out, 1, key);
  return 1;
}

static int l_phys2d_chain_segments(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle chain = lgen_ref_arg(L, 1, "chain", true);
  const LubShapeView *out = NULL;
  int32_t out_count = 0;
  LubStatus st = lub_phys2d_chain_segments(lgen_ctx(), chain, &out, &out_count);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_list_LubShapeView(L, out, out_count);
  return 1;
}

static int l_phys2d_joint(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world", true);
  LubStr key = lgen_str_arg(L, 2);
  LubJointDesc desc_v;
  memset(&desc_v, 0, sizeof desc_v);
  const LubJointDesc *desc = NULL;
  luaL_checktype(L, 3, LUA_TTABLE);
  read_LubJointDesc(L, 3, &desc_v);
  desc = &desc_v;
  LubHandle out = 0;
  LubStatus st = lub_phys2d_joint(lgen_ctx(), world, key, desc, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (out == 0)
    lua_pushnil(L);
  else
    lgen_push_ref_keyed(L, "joint", out, 1, key);
  return 1;
}

static int l_phys2d_joint_info(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle joint = lgen_ref_arg(L, 1, "joint", true);
  LubJointInfo out;
  memset(&out, 0, sizeof out);
  LubStatus st = lub_phys2d_joint_info(lgen_ctx(), joint, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_LubJointInfo(L, &out);
  return 1;
}

static int l_phys2d_joint_force(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle joint = lgen_ref_arg(L, 1, "joint", true);
  LubVec2d out;
  memset(&out, 0, sizeof out);
  LubStatus st = lub_phys2d_joint_force(lgen_ctx(), joint, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_LubVec2d(L, &out);
  return 1;
}

static int l_phys2d_joint_torque(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle joint = lgen_ref_arg(L, 1, "joint", true);
  float out = 0;
  LubStatus st = lub_phys2d_joint_torque(lgen_ctx(), joint, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  lua_pushnumber(L, out);
  return 1;
}

static int l_phys2d_joint_angle(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle joint = lgen_ref_arg(L, 1, "joint", true);
  float out = 0;
  bool has = false;
  LubStatus st = lub_phys2d_joint_angle(lgen_ctx(), joint, &out, &has);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (!has)
    lua_pushnil(L);
  else
    lua_pushnumber(L, out);
  return 1;
}

static int l_phys2d_joint_translation(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle joint = lgen_ref_arg(L, 1, "joint", true);
  float out = 0;
  bool has = false;
  LubStatus st = lub_phys2d_joint_translation(lgen_ctx(), joint, &out, &has);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (!has)
    lua_pushnil(L);
  else
    lua_pushnumber(L, out);
  return 1;
}

static int l_phys2d_joint_speed(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle joint = lgen_ref_arg(L, 1, "joint", true);
  float out = 0;
  bool has = false;
  LubStatus st = lub_phys2d_joint_speed(lgen_ctx(), joint, &out, &has);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (!has)
    lua_pushnil(L);
  else
    lua_pushnumber(L, out);
  return 1;
}

static int l_phys2d_joint_length(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle joint = lgen_ref_arg(L, 1, "joint", true);
  float out = 0;
  bool has = false;
  LubStatus st = lub_phys2d_joint_length(lgen_ctx(), joint, &out, &has);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (!has)
    lua_pushnil(L);
  else
    lua_pushnumber(L, out);
  return 1;
}

static int l_phys2d_joint_motor_force(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle joint = lgen_ref_arg(L, 1, "joint", true);
  float out = 0;
  bool has = false;
  LubStatus st = lub_phys2d_joint_motor_force(lgen_ctx(), joint, &out, &has);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (!has)
    lua_pushnil(L);
  else
    lua_pushnumber(L, out);
  return 1;
}

static int l_phys2d_joint_motor_torque(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle joint = lgen_ref_arg(L, 1, "joint", true);
  float out = 0;
  bool has = false;
  LubStatus st = lub_phys2d_joint_motor_torque(lgen_ctx(), joint, &out, &has);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (!has)
    lua_pushnil(L);
  else
    lua_pushnumber(L, out);
  return 1;
}

static int l_phys2d_joint_set_motor(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle joint = lgen_ref_arg(L, 1, "joint", true);
  LubJointMotorDesc desc_v;
  memset(&desc_v, 0, sizeof desc_v);
  const LubJointMotorDesc *desc = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubJointMotorDesc(L, 2, &desc_v);
  desc = &desc_v;
  LubStatus st = lub_phys2d_joint_set_motor(lgen_ctx(), joint, desc);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

static int l_phys2d_joint_set_limit(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle joint = lgen_ref_arg(L, 1, "joint", true);
  LubJointLimitDesc desc_v;
  memset(&desc_v, 0, sizeof desc_v);
  const LubJointLimitDesc *desc = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubJointLimitDesc(L, 2, &desc_v);
  desc = &desc_v;
  LubStatus st = lub_phys2d_joint_set_limit(lgen_ctx(), joint, desc);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

static int l_phys2d_joint_set_spring(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle joint = lgen_ref_arg(L, 1, "joint", true);
  LubJointSpringDesc desc_v;
  memset(&desc_v, 0, sizeof desc_v);
  const LubJointSpringDesc *desc = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubJointSpringDesc(L, 2, &desc_v);
  desc = &desc_v;
  LubStatus st = lub_phys2d_joint_set_spring(lgen_ctx(), joint, desc);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

static int l_phys2d_joint_set_target(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle joint = lgen_ref_arg(L, 1, "joint", true);
  LubJointTargetDesc desc_v;
  memset(&desc_v, 0, sizeof desc_v);
  const LubJointTargetDesc *desc = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubJointTargetDesc(L, 2, &desc_v);
  desc = &desc_v;
  LubStatus st = lub_phys2d_joint_set_target(lgen_ctx(), joint, desc);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

static int l_phys2d_step(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world", true);
  float dt = (float)luaL_checknumber(L, 2);
  LubStepInfo out;
  memset(&out, 0, sizeof out);
  LubStatus st = lub_phys2d_step(lgen_ctx(), world, dt, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_LubStepInfo(L, &out);
  return 1;
}

static int l_phys2d_pose(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body", true);
  LubPose out;
  memset(&out, 0, sizeof out);
  LubStatus st = lub_phys2d_pose(lgen_ctx(), body, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_LubPose(L, &out);
  return 1;
}

static int l_phys2d_pose_by_key(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world", true);
  LubStr key = lgen_str_arg(L, 2);
  LubPose out;
  memset(&out, 0, sizeof out);
  LubStatus st = lub_phys2d_pose_by_key(lgen_ctx(), world, key, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_LubPose(L, &out);
  return 1;
}

static int l_phys2d_velocity(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body", true);
  LubVelocity out;
  memset(&out, 0, sizeof out);
  LubStatus st = lub_phys2d_velocity(lgen_ctx(), body, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_LubVelocity(L, &out);
  return 1;
}

static int l_phys2d_mass(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body", true);
  LubMassData out;
  memset(&out, 0, sizeof out);
  LubStatus st = lub_phys2d_mass(lgen_ctx(), body, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_LubMassData(L, &out);
  return 1;
}

static int l_phys2d_center(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body", true);
  LubVec2d out;
  memset(&out, 0, sizeof out);
  LubStatus st = lub_phys2d_center(lgen_ctx(), body, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_LubVec2d(L, &out);
  return 1;
}

static int l_phys2d_world_point(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body", true);
  LubVec2d local_point_v;
  memset(&local_point_v, 0, sizeof local_point_v);
  const LubVec2d *local_point = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubVec2d(L, 2, &local_point_v);
  local_point = &local_point_v;
  LubVec2d out;
  memset(&out, 0, sizeof out);
  LubStatus st = lub_phys2d_world_point(lgen_ctx(), body, local_point, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_LubVec2d(L, &out);
  return 1;
}

static int l_phys2d_local_point(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body", true);
  LubVec2d world_point_v;
  memset(&world_point_v, 0, sizeof world_point_v);
  const LubVec2d *world_point = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubVec2d(L, 2, &world_point_v);
  world_point = &world_point_v;
  LubVec2d out;
  memset(&out, 0, sizeof out);
  LubStatus st = lub_phys2d_local_point(lgen_ctx(), body, world_point, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_LubVec2d(L, &out);
  return 1;
}

static int l_phys2d_velocity_at(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body", true);
  LubVec2d world_point_v;
  memset(&world_point_v, 0, sizeof world_point_v);
  const LubVec2d *world_point = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubVec2d(L, 2, &world_point_v);
  world_point = &world_point_v;
  LubVec2d out;
  memset(&out, 0, sizeof out);
  LubStatus st = lub_phys2d_velocity_at(lgen_ctx(), body, world_point, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_LubVec2d(L, &out);
  return 1;
}

static int l_phys2d_body_shapes(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body", true);
  const LubShapeView *out = NULL;
  int32_t out_count = 0;
  LubStatus st = lub_phys2d_body_shapes(lgen_ctx(), body, &out, &out_count);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_list_LubShapeView(L, out, out_count);
  return 1;
}

static int l_phys2d_body_joints(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body", true);
  const LubJointView *out = NULL;
  int32_t out_count = 0;
  LubStatus st = lub_phys2d_body_joints(lgen_ctx(), body, &out, &out_count);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_list_LubJointView(L, out, out_count);
  return 1;
}

static int l_phys2d_body_contacts(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body", true);
  const LubContactData *out = NULL;
  int32_t out_count = 0;
  LubStatus st = lub_phys2d_body_contacts(lgen_ctx(), body, &out, &out_count);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_list_LubContactData(L, out, out_count);
  return 1;
}

static int l_phys2d_shape_test_point(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle shape = lgen_ref_arg(L, 1, "shape", true);
  LubVec2d point_v;
  memset(&point_v, 0, sizeof point_v);
  const LubVec2d *point = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubVec2d(L, 2, &point_v);
  point = &point_v;
  bool out = false;
  LubStatus st = lub_phys2d_shape_test_point(lgen_ctx(), shape, point, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  lua_pushboolean(L, out);
  return 1;
}

static int l_phys2d_shape_raycast(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle shape = lgen_ref_arg(L, 1, "shape", true);
  LubRaycastDesc query_v;
  memset(&query_v, 0, sizeof query_v);
  const LubRaycastDesc *query = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubRaycastDesc(L, 2, &query_v);
  query = &query_v;
  LubShapeRayHit out;
  memset(&out, 0, sizeof out);
  bool has = false;
  LubStatus st = lub_phys2d_shape_raycast(lgen_ctx(), shape, query, &out, &has);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (!has)
    lua_pushnil(L);
  else
    push_LubShapeRayHit(L, &out);
  return 1;
}

static int l_phys2d_shape_closest_point(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle shape = lgen_ref_arg(L, 1, "shape", true);
  LubVec2d point_v;
  memset(&point_v, 0, sizeof point_v);
  const LubVec2d *point = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubVec2d(L, 2, &point_v);
  point = &point_v;
  LubVec2d out;
  memset(&out, 0, sizeof out);
  LubStatus st = lub_phys2d_shape_closest_point(lgen_ctx(), shape, point, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_LubVec2d(L, &out);
  return 1;
}

static int l_phys2d_shape_aabb(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle shape = lgen_ref_arg(L, 1, "shape", true);
  LubAabb out;
  memset(&out, 0, sizeof out);
  LubStatus st = lub_phys2d_shape_aabb(lgen_ctx(), shape, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_LubAabb(L, &out);
  return 1;
}

static int l_phys2d_shape_info(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle shape = lgen_ref_arg(L, 1, "shape", true);
  LubShapeInfo out;
  memset(&out, 0, sizeof out);
  LubStatus st = lub_phys2d_shape_info(lgen_ctx(), shape, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_LubShapeInfo(L, &out);
  return 1;
}

static int l_phys2d_shape_set_material(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle shape = lgen_ref_arg(L, 1, "shape", true);
  LubMaterialDesc desc_v;
  memset(&desc_v, 0, sizeof desc_v);
  const LubMaterialDesc *desc = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubMaterialDesc(L, 2, &desc_v);
  desc = &desc_v;
  LubStatus st = lub_phys2d_shape_set_material(lgen_ctx(), shape, desc);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

static int l_phys2d_shape_set_filter(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle shape = lgen_ref_arg(L, 1, "shape", true);
  LubFilterDesc filter_v;
  memset(&filter_v, 0, sizeof filter_v);
  const LubFilterDesc *filter = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubFilterDesc(L, 2, &filter_v);
  filter = &filter_v;
  LubStatus st = lub_phys2d_shape_set_filter(lgen_ctx(), shape, filter);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

static int l_phys2d_shape_set_events(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle shape = lgen_ref_arg(L, 1, "shape", true);
  LubShapeEventsDesc desc_v;
  memset(&desc_v, 0, sizeof desc_v);
  const LubShapeEventsDesc *desc = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubShapeEventsDesc(L, 2, &desc_v);
  desc = &desc_v;
  LubStatus st = lub_phys2d_shape_set_events(lgen_ctx(), shape, desc);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

static int l_phys2d_contacts(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world", true);
  int32_t kind_v = 0;
  const int32_t *kind = NULL;
  if (!lua_isnoneornil(L, 2)) {
    kind_v = lgen_enum_str_arg(L, 2, names_LubPhys2dEventKind,
                               values_LubPhys2dEventKind, "EventKind");
    kind = &kind_v;
  }
  const LubContactEvent *out = NULL;
  int32_t out_count = 0;
  LubStatus st = lub_phys2d_contacts(lgen_ctx(), world, kind, &out, &out_count);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_list_LubContactEvent(L, out, out_count);
  return 1;
}

static int l_phys2d_body_events(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world", true);
  const LubBodyEvent *out = NULL;
  int32_t out_count = 0;
  LubStatus st = lub_phys2d_body_events(lgen_ctx(), world, &out, &out_count);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_list_LubBodyEvent(L, out, out_count);
  return 1;
}

static int l_phys2d_sensors(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world", true);
  int32_t kind_v = 0;
  const int32_t *kind = NULL;
  if (!lua_isnoneornil(L, 2)) {
    kind_v = lgen_enum_str_arg(L, 2, names_LubPhys2dEventKind,
                               values_LubPhys2dEventKind, "EventKind");
    kind = &kind_v;
  }
  const LubSensorEvent *out = NULL;
  int32_t out_count = 0;
  LubStatus st = lub_phys2d_sensors(lgen_ctx(), world, kind, &out, &out_count);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_list_LubSensorEvent(L, out, out_count);
  return 1;
}

static int l_phys2d_raycast(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world", true);
  LubRaycastDesc query_v;
  memset(&query_v, 0, sizeof query_v);
  const LubRaycastDesc *query = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubRaycastDesc(L, 2, &query_v);
  query = &query_v;
  LubRayHit out;
  memset(&out, 0, sizeof out);
  bool has = false;
  LubStatus st = lub_phys2d_raycast(lgen_ctx(), world, query, &out, &has);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (!has)
    lua_pushnil(L);
  else
    push_LubRayHit(L, &out);
  return 1;
}

static int l_phys2d_raycast_all(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world", true);
  LubRaycastDesc query_v;
  memset(&query_v, 0, sizeof query_v);
  const LubRaycastDesc *query = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubRaycastDesc(L, 2, &query_v);
  query = &query_v;
  LgenCallbacks *visitor_cb = lgen_callbacks_arg(L, 3);
  const LubRayHit *out = NULL;
  int32_t out_count = 0;
  LubStatus st = lub_phys2d_raycast_all(
      lgen_ctx(), world, query,
      visitor_cb ? tramp_l_phys2d_raycast_all_visitor : NULL, visitor_cb, &out,
      &out_count);
  if (lgen_callbacks_error(visitor_cb)) {
    lua_pushnil(L);
    lua_pushfstring(L, "phys2d_raycast_all visitor: %s",
                    lgen_callbacks_error(visitor_cb));
    lgen_callbacks_free(visitor_cb);
    lgen_release(mark);
    return 2;
  }
  lgen_callbacks_free(visitor_cb);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_list_LubRayHit(L, out, out_count);
  return 1;
}

static int l_phys2d_overlap_aabb(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world", true);
  LubAabbDesc query_v;
  memset(&query_v, 0, sizeof query_v);
  const LubAabbDesc *query = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubAabbDesc(L, 2, &query_v);
  query = &query_v;
  LgenCallbacks *visitor_cb = lgen_callbacks_arg(L, 3);
  const LubShapeView *out = NULL;
  int32_t out_count = 0;
  LubStatus st = lub_phys2d_overlap_aabb(
      lgen_ctx(), world, query,
      visitor_cb ? tramp_l_phys2d_overlap_aabb_visitor : NULL, visitor_cb, &out,
      &out_count);
  if (lgen_callbacks_error(visitor_cb)) {
    lua_pushnil(L);
    lua_pushfstring(L, "phys2d_overlap_aabb visitor: %s",
                    lgen_callbacks_error(visitor_cb));
    lgen_callbacks_free(visitor_cb);
    lgen_release(mark);
    return 2;
  }
  lgen_callbacks_free(visitor_cb);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_list_LubShapeView(L, out, out_count);
  return 1;
}

static int l_phys2d_shape_cast(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world", true);
  LubShapeCastDesc query_v;
  memset(&query_v, 0, sizeof query_v);
  const LubShapeCastDesc *query = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubShapeCastDesc(L, 2, &query_v);
  query = &query_v;
  LubRayHit out;
  memset(&out, 0, sizeof out);
  bool has = false;
  LubStatus st = lub_phys2d_shape_cast(lgen_ctx(), world, query, &out, &has);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (!has)
    lua_pushnil(L);
  else
    push_LubRayHit(L, &out);
  return 1;
}

static int l_phys2d_shape_cast_all(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world", true);
  LubShapeCastDesc query_v;
  memset(&query_v, 0, sizeof query_v);
  const LubShapeCastDesc *query = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubShapeCastDesc(L, 2, &query_v);
  query = &query_v;
  LgenCallbacks *visitor_cb = lgen_callbacks_arg(L, 3);
  const LubRayHit *out = NULL;
  int32_t out_count = 0;
  LubStatus st = lub_phys2d_shape_cast_all(
      lgen_ctx(), world, query,
      visitor_cb ? tramp_l_phys2d_shape_cast_all_visitor : NULL, visitor_cb,
      &out, &out_count);
  if (lgen_callbacks_error(visitor_cb)) {
    lua_pushnil(L);
    lua_pushfstring(L, "phys2d_shape_cast_all visitor: %s",
                    lgen_callbacks_error(visitor_cb));
    lgen_callbacks_free(visitor_cb);
    lgen_release(mark);
    return 2;
  }
  lgen_callbacks_free(visitor_cb);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_list_LubRayHit(L, out, out_count);
  return 1;
}

static int l_phys2d_cast_mover(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world", true);
  LubMoverDesc query_v;
  memset(&query_v, 0, sizeof query_v);
  const LubMoverDesc *query = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubMoverDesc(L, 2, &query_v);
  query = &query_v;
  LubMoverCast out;
  memset(&out, 0, sizeof out);
  bool has = false;
  LubStatus st = lub_phys2d_cast_mover(lgen_ctx(), world, query, &out, &has);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (!has)
    lua_pushnil(L);
  else
    push_LubMoverCast(L, &out);
  return 1;
}

static int l_phys2d_collide_mover(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world", true);
  LubMoverDesc query_v;
  memset(&query_v, 0, sizeof query_v);
  const LubMoverDesc *query = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubMoverDesc(L, 2, &query_v);
  query = &query_v;
  LgenCallbacks *visitor_cb = lgen_callbacks_arg(L, 3);
  const LubMoverPlane *out = NULL;
  int32_t out_count = 0;
  LubStatus st = lub_phys2d_collide_mover(
      lgen_ctx(), world, query,
      visitor_cb ? tramp_l_phys2d_collide_mover_visitor : NULL, visitor_cb,
      &out, &out_count);
  if (lgen_callbacks_error(visitor_cb)) {
    lua_pushnil(L);
    lua_pushfstring(L, "phys2d_collide_mover visitor: %s",
                    lgen_callbacks_error(visitor_cb));
    lgen_callbacks_free(visitor_cb);
    lgen_release(mark);
    return 2;
  }
  lgen_callbacks_free(visitor_cb);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_list_LubMoverPlane(L, out, out_count);
  return 1;
}

static int l_phys2d_explode(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world", true);
  LubExplosionDesc desc_v;
  memset(&desc_v, 0, sizeof desc_v);
  const LubExplosionDesc *desc = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubExplosionDesc(L, 2, &desc_v);
  desc = &desc_v;
  LubStatus st = lub_phys2d_explode(lgen_ctx(), world, desc);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

static int l_phys2d_debug(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world", true);
  LubDebugOpts opts_v;
  memset(&opts_v, 0, sizeof opts_v);
  const LubDebugOpts *opts = NULL;
  if (!lua_isnoneornil(L, 2)) {
    luaL_checktype(L, 2, LUA_TTABLE);
    read_LubDebugOpts(L, 2, &opts_v);
    opts = &opts_v;
  }
  LubDebugData out;
  memset(&out, 0, sizeof out);
  LubStatus st = lub_phys2d_debug(lgen_ctx(), world, opts, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_LubDebugData(L, &out);
  return 1;
}

static int l_phys2d_profile(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world", true);
  LubProfile out;
  memset(&out, 0, sizeof out);
  LubStatus st = lub_phys2d_profile(lgen_ctx(), world, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_LubProfile(L, &out);
  return 1;
}

static int l_phys2d_counters(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world", true);
  LubCounters out;
  memset(&out, 0, sizeof out);
  LubStatus st = lub_phys2d_counters(lgen_ctx(), world, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_LubCounters(L, &out);
  return 1;
}

static int l_phys2d_add_force(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body", true);
  LubVec2d force_v;
  memset(&force_v, 0, sizeof force_v);
  const LubVec2d *force = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubVec2d(L, 2, &force_v);
  force = &force_v;
  LubCommandOpts opts_v;
  memset(&opts_v, 0, sizeof opts_v);
  const LubCommandOpts *opts = NULL;
  if (!lua_isnoneornil(L, 3)) {
    luaL_checktype(L, 3, LUA_TTABLE);
    read_LubCommandOpts(L, 3, &opts_v);
    opts = &opts_v;
  }
  LubStatus st = lub_phys2d_add_force(lgen_ctx(), body, force, opts);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

static int l_phys2d_add_force_center(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body", true);
  LubVec2d force_v;
  memset(&force_v, 0, sizeof force_v);
  const LubVec2d *force = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubVec2d(L, 2, &force_v);
  force = &force_v;
  LubCommandOpts opts_v;
  memset(&opts_v, 0, sizeof opts_v);
  const LubCommandOpts *opts = NULL;
  if (!lua_isnoneornil(L, 3)) {
    luaL_checktype(L, 3, LUA_TTABLE);
    read_LubCommandOpts(L, 3, &opts_v);
    opts = &opts_v;
  }
  LubStatus st = lub_phys2d_add_force_center(lgen_ctx(), body, force, opts);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

static int l_phys2d_add_impulse(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body", true);
  LubVec2d impulse_v;
  memset(&impulse_v, 0, sizeof impulse_v);
  const LubVec2d *impulse = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubVec2d(L, 2, &impulse_v);
  impulse = &impulse_v;
  LubCommandOpts opts_v;
  memset(&opts_v, 0, sizeof opts_v);
  const LubCommandOpts *opts = NULL;
  if (!lua_isnoneornil(L, 3)) {
    luaL_checktype(L, 3, LUA_TTABLE);
    read_LubCommandOpts(L, 3, &opts_v);
    opts = &opts_v;
  }
  LubStatus st = lub_phys2d_add_impulse(lgen_ctx(), body, impulse, opts);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

static int l_phys2d_add_impulse_center(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body", true);
  LubVec2d impulse_v;
  memset(&impulse_v, 0, sizeof impulse_v);
  const LubVec2d *impulse = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubVec2d(L, 2, &impulse_v);
  impulse = &impulse_v;
  LubCommandOpts opts_v;
  memset(&opts_v, 0, sizeof opts_v);
  const LubCommandOpts *opts = NULL;
  if (!lua_isnoneornil(L, 3)) {
    luaL_checktype(L, 3, LUA_TTABLE);
    read_LubCommandOpts(L, 3, &opts_v);
    opts = &opts_v;
  }
  LubStatus st = lub_phys2d_add_impulse_center(lgen_ctx(), body, impulse, opts);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

static int l_phys2d_add_torque(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body", true);
  float torque = (float)luaL_checknumber(L, 2);
  LubCommandOpts opts_v;
  memset(&opts_v, 0, sizeof opts_v);
  const LubCommandOpts *opts = NULL;
  if (!lua_isnoneornil(L, 3)) {
    luaL_checktype(L, 3, LUA_TTABLE);
    read_LubCommandOpts(L, 3, &opts_v);
    opts = &opts_v;
  }
  LubStatus st = lub_phys2d_add_torque(lgen_ctx(), body, torque, opts);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

static int l_phys2d_add_angular_impulse(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body", true);
  float impulse = (float)luaL_checknumber(L, 2);
  LubCommandOpts opts_v;
  memset(&opts_v, 0, sizeof opts_v);
  const LubCommandOpts *opts = NULL;
  if (!lua_isnoneornil(L, 3)) {
    luaL_checktype(L, 3, LUA_TTABLE);
    read_LubCommandOpts(L, 3, &opts_v);
    opts = &opts_v;
  }
  LubStatus st =
      lub_phys2d_add_angular_impulse(lgen_ctx(), body, impulse, opts);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

static int l_phys2d_set_velocity(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body", true);
  LubVelocityDesc velocity_v;
  memset(&velocity_v, 0, sizeof velocity_v);
  const LubVelocityDesc *velocity = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubVelocityDesc(L, 2, &velocity_v);
  velocity = &velocity_v;
  LubCommandOpts opts_v;
  memset(&opts_v, 0, sizeof opts_v);
  const LubCommandOpts *opts = NULL;
  if (!lua_isnoneornil(L, 3)) {
    luaL_checktype(L, 3, LUA_TTABLE);
    read_LubCommandOpts(L, 3, &opts_v);
    opts = &opts_v;
  }
  LubStatus st = lub_phys2d_set_velocity(lgen_ctx(), body, velocity, opts);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

static int l_phys2d_teleport(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body", true);
  LubPoseDesc pose_v;
  memset(&pose_v, 0, sizeof pose_v);
  const LubPoseDesc *pose = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubPoseDesc(L, 2, &pose_v);
  pose = &pose_v;
  LubCommandOpts opts_v;
  memset(&opts_v, 0, sizeof opts_v);
  const LubCommandOpts *opts = NULL;
  if (!lua_isnoneornil(L, 3)) {
    luaL_checktype(L, 3, LUA_TTABLE);
    read_LubCommandOpts(L, 3, &opts_v);
    opts = &opts_v;
  }
  LubStatus st = lub_phys2d_teleport(lgen_ctx(), body, pose, opts);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

static int l_phys2d_set_target(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body", true);
  LubPoseDesc target_v;
  memset(&target_v, 0, sizeof target_v);
  const LubPoseDesc *target = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubPoseDesc(L, 2, &target_v);
  target = &target_v;
  LubCommandOpts opts_v;
  memset(&opts_v, 0, sizeof opts_v);
  const LubCommandOpts *opts = NULL;
  if (!lua_isnoneornil(L, 3)) {
    luaL_checktype(L, 3, LUA_TTABLE);
    read_LubCommandOpts(L, 3, &opts_v);
    opts = &opts_v;
  }
  LubStatus st = lub_phys2d_set_target(lgen_ctx(), body, target, opts);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

static int l_phys2d_set_mass_data(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body", true);
  LubMassDataDesc mass_data_v;
  memset(&mass_data_v, 0, sizeof mass_data_v);
  const LubMassDataDesc *mass_data = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubMassDataDesc(L, 2, &mass_data_v);
  mass_data = &mass_data_v;
  LubCommandOpts opts_v;
  memset(&opts_v, 0, sizeof opts_v);
  const LubCommandOpts *opts = NULL;
  if (!lua_isnoneornil(L, 3)) {
    luaL_checktype(L, 3, LUA_TTABLE);
    read_LubCommandOpts(L, 3, &opts_v);
    opts = &opts_v;
  }
  LubStatus st = lub_phys2d_set_mass_data(lgen_ctx(), body, mass_data, opts);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

static int l_phys3d_find_world(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr key = lgen_str_arg(L, 1);
  LubHandle out = lub_phys3d_find_world(lgen_ctx(), key);
  lgen_release(mark);
  if (out == 0)
    lua_pushnil(L);
  else
    lgen_push_ref_keyed(L, "world3d", out, 0, key);
  return 1;
}

static int l_phys3d_find_body(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world3d", true);
  LubStr key = lgen_str_arg(L, 2);
  LubHandle out = lub_phys3d_find_body(lgen_ctx(), world, key);
  lgen_release(mark);
  if (out == 0)
    lua_pushnil(L);
  else
    lgen_push_ref_keyed(L, "body3d", out, 1, key);
  return 1;
}

static int l_phys3d_find_shape(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body3d", true);
  LubStr key = lgen_str_arg(L, 2);
  LubHandle out = lub_phys3d_find_shape(lgen_ctx(), body, key);
  lgen_release(mark);
  if (out == 0)
    lua_pushnil(L);
  else
    lgen_push_ref_keyed(L, "shape3d", out, 1, key);
  return 1;
}

static int l_phys3d_find_joint(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world3d", true);
  LubStr key = lgen_str_arg(L, 2);
  LubHandle out = lub_phys3d_find_joint(lgen_ctx(), world, key);
  lgen_release(mark);
  if (out == 0)
    lua_pushnil(L);
  else
    lgen_push_ref_keyed(L, "joint3d", out, 1, key);
  return 1;
}

static int l_phys3d_world(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr key = lgen_str_arg(L, 1);
  LubWorldOpts3d opts_v;
  memset(&opts_v, 0, sizeof opts_v);
  const LubWorldOpts3d *opts = NULL;
  if (!lua_isnoneornil(L, 2)) {
    luaL_checktype(L, 2, LUA_TTABLE);
    read_LubWorldOpts3d(L, 2, &opts_v);
    opts = &opts_v;
  }
  LubHandle out = 0;
  LubStatus st = lub_phys3d_world(lgen_ctx(), key, opts, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (out == 0)
    lua_pushnil(L);
  else
    lgen_push_ref_keyed(L, "world3d", out, 0, key);
  return 1;
}

static int l_phys3d_begin(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world3d", true);
  LubBeginOpts3d opts_v;
  memset(&opts_v, 0, sizeof opts_v);
  const LubBeginOpts3d *opts = NULL;
  if (!lua_isnoneornil(L, 2)) {
    luaL_checktype(L, 2, LUA_TTABLE);
    read_LubBeginOpts3d(L, 2, &opts_v);
    opts = &opts_v;
  }
  LubStatus st = lub_phys3d_begin(lgen_ctx(), world, opts);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

static int l_phys3d_world_info(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world3d", true);
  LubWorldInfo3d out;
  memset(&out, 0, sizeof out);
  LubStatus st = lub_phys3d_world_info(lgen_ctx(), world, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_LubWorldInfo3d(L, &out);
  return 1;
}

static int l_phys3d_body(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world3d", true);
  LubStr key = lgen_str_arg(L, 2);
  LubBodyDesc3d desc_v;
  memset(&desc_v, 0, sizeof desc_v);
  const LubBodyDesc3d *desc = NULL;
  luaL_checktype(L, 3, LUA_TTABLE);
  read_LubBodyDesc3d(L, 3, &desc_v);
  desc = &desc_v;
  LubHandle out = 0;
  LubStatus st = lub_phys3d_body(lgen_ctx(), world, key, desc, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (out == 0)
    lua_pushnil(L);
  else
    lgen_push_ref_keyed(L, "body3d", out, 1, key);
  return 1;
}

static int l_phys3d_sphere(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body3d", true);
  LubStr key = lgen_str_arg(L, 2);
  LubSphereDesc3d desc_v;
  memset(&desc_v, 0, sizeof desc_v);
  const LubSphereDesc3d *desc = NULL;
  luaL_checktype(L, 3, LUA_TTABLE);
  read_LubSphereDesc3d(L, 3, &desc_v);
  desc = &desc_v;
  LubHandle out = 0;
  LubStatus st = lub_phys3d_sphere(lgen_ctx(), body, key, desc, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (out == 0)
    lua_pushnil(L);
  else
    lgen_push_ref_keyed(L, "shape3d", out, 1, key);
  return 1;
}

static int l_phys3d_box(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body3d", true);
  LubStr key = lgen_str_arg(L, 2);
  LubBoxDesc3d desc_v;
  memset(&desc_v, 0, sizeof desc_v);
  const LubBoxDesc3d *desc = NULL;
  luaL_checktype(L, 3, LUA_TTABLE);
  read_LubBoxDesc3d(L, 3, &desc_v);
  desc = &desc_v;
  LubHandle out = 0;
  LubStatus st = lub_phys3d_box(lgen_ctx(), body, key, desc, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (out == 0)
    lua_pushnil(L);
  else
    lgen_push_ref_keyed(L, "shape3d", out, 1, key);
  return 1;
}

static int l_phys3d_capsule(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body3d", true);
  LubStr key = lgen_str_arg(L, 2);
  LubCapsuleDesc3d desc_v;
  memset(&desc_v, 0, sizeof desc_v);
  const LubCapsuleDesc3d *desc = NULL;
  luaL_checktype(L, 3, LUA_TTABLE);
  read_LubCapsuleDesc3d(L, 3, &desc_v);
  desc = &desc_v;
  LubHandle out = 0;
  LubStatus st = lub_phys3d_capsule(lgen_ctx(), body, key, desc, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (out == 0)
    lua_pushnil(L);
  else
    lgen_push_ref_keyed(L, "shape3d", out, 1, key);
  return 1;
}

static int l_phys3d_cylinder(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body3d", true);
  LubStr key = lgen_str_arg(L, 2);
  LubCylinderDesc3d desc_v;
  memset(&desc_v, 0, sizeof desc_v);
  const LubCylinderDesc3d *desc = NULL;
  luaL_checktype(L, 3, LUA_TTABLE);
  read_LubCylinderDesc3d(L, 3, &desc_v);
  desc = &desc_v;
  LubHandle out = 0;
  LubStatus st = lub_phys3d_cylinder(lgen_ctx(), body, key, desc, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (out == 0)
    lua_pushnil(L);
  else
    lgen_push_ref_keyed(L, "shape3d", out, 1, key);
  return 1;
}

static int l_phys3d_cone(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body3d", true);
  LubStr key = lgen_str_arg(L, 2);
  LubConeDesc3d desc_v;
  memset(&desc_v, 0, sizeof desc_v);
  const LubConeDesc3d *desc = NULL;
  luaL_checktype(L, 3, LUA_TTABLE);
  read_LubConeDesc3d(L, 3, &desc_v);
  desc = &desc_v;
  LubHandle out = 0;
  LubStatus st = lub_phys3d_cone(lgen_ctx(), body, key, desc, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (out == 0)
    lua_pushnil(L);
  else
    lgen_push_ref_keyed(L, "shape3d", out, 1, key);
  return 1;
}

static int l_phys3d_hull(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body3d", true);
  LubStr key = lgen_str_arg(L, 2);
  LubHullDesc3d desc_v;
  memset(&desc_v, 0, sizeof desc_v);
  const LubHullDesc3d *desc = NULL;
  luaL_checktype(L, 3, LUA_TTABLE);
  read_LubHullDesc3d(L, 3, &desc_v);
  desc = &desc_v;
  LubHandle out = 0;
  LubStatus st = lub_phys3d_hull(lgen_ctx(), body, key, desc, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (out == 0)
    lua_pushnil(L);
  else
    lgen_push_ref_keyed(L, "shape3d", out, 1, key);
  return 1;
}

static int l_phys3d_mesh(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body3d", true);
  LubStr key = lgen_str_arg(L, 2);
  LubMeshDesc3d desc_v;
  memset(&desc_v, 0, sizeof desc_v);
  const LubMeshDesc3d *desc = NULL;
  luaL_checktype(L, 3, LUA_TTABLE);
  read_LubMeshDesc3d(L, 3, &desc_v);
  desc = &desc_v;
  LubHandle out = 0;
  LubStatus st = lub_phys3d_mesh(lgen_ctx(), body, key, desc, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (out == 0)
    lua_pushnil(L);
  else
    lgen_push_ref_keyed(L, "shape3d", out, 1, key);
  return 1;
}

static int l_phys3d_height_field(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body3d", true);
  LubStr key = lgen_str_arg(L, 2);
  LubHeightFieldDesc3d desc_v;
  memset(&desc_v, 0, sizeof desc_v);
  const LubHeightFieldDesc3d *desc = NULL;
  luaL_checktype(L, 3, LUA_TTABLE);
  read_LubHeightFieldDesc3d(L, 3, &desc_v);
  desc = &desc_v;
  LubHandle out = 0;
  LubStatus st = lub_phys3d_height_field(lgen_ctx(), body, key, desc, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (out == 0)
    lua_pushnil(L);
  else
    lgen_push_ref_keyed(L, "shape3d", out, 1, key);
  return 1;
}

static int l_phys3d_compound(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body3d", true);
  LubStr key = lgen_str_arg(L, 2);
  LubCompoundDesc3d desc_v;
  memset(&desc_v, 0, sizeof desc_v);
  const LubCompoundDesc3d *desc = NULL;
  luaL_checktype(L, 3, LUA_TTABLE);
  read_LubCompoundDesc3d(L, 3, &desc_v);
  desc = &desc_v;
  LubHandle out = 0;
  LubStatus st = lub_phys3d_compound(lgen_ctx(), body, key, desc, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (out == 0)
    lua_pushnil(L);
  else
    lgen_push_ref_keyed(L, "shape3d", out, 1, key);
  return 1;
}

static int l_phys3d_joint(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world3d", true);
  LubStr key = lgen_str_arg(L, 2);
  LubJointDesc3d desc_v;
  memset(&desc_v, 0, sizeof desc_v);
  const LubJointDesc3d *desc = NULL;
  luaL_checktype(L, 3, LUA_TTABLE);
  read_LubJointDesc3d(L, 3, &desc_v);
  desc = &desc_v;
  LubHandle out = 0;
  LubStatus st = lub_phys3d_joint(lgen_ctx(), world, key, desc, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (out == 0)
    lua_pushnil(L);
  else
    lgen_push_ref_keyed(L, "joint3d", out, 1, key);
  return 1;
}

static int l_phys3d_joint_info(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle joint = lgen_ref_arg(L, 1, "joint3d", true);
  LubJointInfo3d out;
  memset(&out, 0, sizeof out);
  LubStatus st = lub_phys3d_joint_info(lgen_ctx(), joint, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_LubJointInfo3d(L, &out);
  return 1;
}

static int l_phys3d_joint_force(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle joint = lgen_ref_arg(L, 1, "joint3d", true);
  LubVec3d out;
  memset(&out, 0, sizeof out);
  LubStatus st = lub_phys3d_joint_force(lgen_ctx(), joint, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_LubVec3d(L, &out);
  return 1;
}

static int l_phys3d_joint_torque(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle joint = lgen_ref_arg(L, 1, "joint3d", true);
  LubVec3d out;
  memset(&out, 0, sizeof out);
  LubStatus st = lub_phys3d_joint_torque(lgen_ctx(), joint, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_LubVec3d(L, &out);
  return 1;
}

static int l_phys3d_joint_angle(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle joint = lgen_ref_arg(L, 1, "joint3d", true);
  float out = 0;
  bool has = false;
  LubStatus st = lub_phys3d_joint_angle(lgen_ctx(), joint, &out, &has);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (!has)
    lua_pushnil(L);
  else
    lua_pushnumber(L, out);
  return 1;
}

static int l_phys3d_joint_translation(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle joint = lgen_ref_arg(L, 1, "joint3d", true);
  float out = 0;
  bool has = false;
  LubStatus st = lub_phys3d_joint_translation(lgen_ctx(), joint, &out, &has);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (!has)
    lua_pushnil(L);
  else
    lua_pushnumber(L, out);
  return 1;
}

static int l_phys3d_joint_speed(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle joint = lgen_ref_arg(L, 1, "joint3d", true);
  float out = 0;
  bool has = false;
  LubStatus st = lub_phys3d_joint_speed(lgen_ctx(), joint, &out, &has);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (!has)
    lua_pushnil(L);
  else
    lua_pushnumber(L, out);
  return 1;
}

static int l_phys3d_joint_length(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle joint = lgen_ref_arg(L, 1, "joint3d", true);
  float out = 0;
  bool has = false;
  LubStatus st = lub_phys3d_joint_length(lgen_ctx(), joint, &out, &has);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (!has)
    lua_pushnil(L);
  else
    lua_pushnumber(L, out);
  return 1;
}

static int l_phys3d_joint_motor_force(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle joint = lgen_ref_arg(L, 1, "joint3d", true);
  float out = 0;
  bool has = false;
  LubStatus st = lub_phys3d_joint_motor_force(lgen_ctx(), joint, &out, &has);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (!has)
    lua_pushnil(L);
  else
    lua_pushnumber(L, out);
  return 1;
}

static int l_phys3d_joint_motor_torque(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle joint = lgen_ref_arg(L, 1, "joint3d", true);
  float out = 0;
  bool has = false;
  LubStatus st = lub_phys3d_joint_motor_torque(lgen_ctx(), joint, &out, &has);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (!has)
    lua_pushnil(L);
  else
    lua_pushnumber(L, out);
  return 1;
}

static int l_phys3d_joint_motor_torque_vector(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle joint = lgen_ref_arg(L, 1, "joint3d", true);
  LubVec3d out;
  memset(&out, 0, sizeof out);
  bool has = false;
  LubStatus st =
      lub_phys3d_joint_motor_torque_vector(lgen_ctx(), joint, &out, &has);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (!has)
    lua_pushnil(L);
  else
    push_LubVec3d(L, &out);
  return 1;
}

static int l_phys3d_joint_set_motor(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle joint = lgen_ref_arg(L, 1, "joint3d", true);
  LubJointMotorDesc3d desc_v;
  memset(&desc_v, 0, sizeof desc_v);
  const LubJointMotorDesc3d *desc = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubJointMotorDesc3d(L, 2, &desc_v);
  desc = &desc_v;
  LubStatus st = lub_phys3d_joint_set_motor(lgen_ctx(), joint, desc);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

static int l_phys3d_joint_set_limit(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle joint = lgen_ref_arg(L, 1, "joint3d", true);
  LubJointLimitDesc3d desc_v;
  memset(&desc_v, 0, sizeof desc_v);
  const LubJointLimitDesc3d *desc = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubJointLimitDesc3d(L, 2, &desc_v);
  desc = &desc_v;
  LubStatus st = lub_phys3d_joint_set_limit(lgen_ctx(), joint, desc);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

static int l_phys3d_joint_set_spring(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle joint = lgen_ref_arg(L, 1, "joint3d", true);
  LubJointSpringDesc3d desc_v;
  memset(&desc_v, 0, sizeof desc_v);
  const LubJointSpringDesc3d *desc = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubJointSpringDesc3d(L, 2, &desc_v);
  desc = &desc_v;
  LubStatus st = lub_phys3d_joint_set_spring(lgen_ctx(), joint, desc);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

static int l_phys3d_joint_set_target(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle joint = lgen_ref_arg(L, 1, "joint3d", true);
  LubJointTargetDesc3d desc_v;
  memset(&desc_v, 0, sizeof desc_v);
  const LubJointTargetDesc3d *desc = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubJointTargetDesc3d(L, 2, &desc_v);
  desc = &desc_v;
  LubStatus st = lub_phys3d_joint_set_target(lgen_ctx(), joint, desc);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

static int l_phys3d_body_joints(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body3d", true);
  const LubJointView3d *out = NULL;
  int32_t out_count = 0;
  LubStatus st = lub_phys3d_body_joints(lgen_ctx(), body, &out, &out_count);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_list_LubJointView3d(L, out, out_count);
  return 1;
}

static int l_phys3d_cast_mover(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world3d", true);
  LubMoverDesc3d query_v;
  memset(&query_v, 0, sizeof query_v);
  const LubMoverDesc3d *query = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubMoverDesc3d(L, 2, &query_v);
  query = &query_v;
  LubMoverCast3d out;
  memset(&out, 0, sizeof out);
  bool has = false;
  LubStatus st = lub_phys3d_cast_mover(lgen_ctx(), world, query, &out, &has);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (!has)
    lua_pushnil(L);
  else
    push_LubMoverCast3d(L, &out);
  return 1;
}

static int l_phys3d_collide_mover(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world3d", true);
  LubMoverDesc3d query_v;
  memset(&query_v, 0, sizeof query_v);
  const LubMoverDesc3d *query = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubMoverDesc3d(L, 2, &query_v);
  query = &query_v;
  LgenCallbacks *visitor_cb = lgen_callbacks_arg(L, 3);
  const LubMoverPlane3d *out = NULL;
  int32_t out_count = 0;
  LubStatus st = lub_phys3d_collide_mover(
      lgen_ctx(), world, query,
      visitor_cb ? tramp_l_phys3d_collide_mover_visitor : NULL, visitor_cb,
      &out, &out_count);
  if (lgen_callbacks_error(visitor_cb)) {
    lua_pushnil(L);
    lua_pushfstring(L, "phys3d_collide_mover visitor: %s",
                    lgen_callbacks_error(visitor_cb));
    lgen_callbacks_free(visitor_cb);
    lgen_release(mark);
    return 2;
  }
  lgen_callbacks_free(visitor_cb);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_list_LubMoverPlane3d(L, out, out_count);
  return 1;
}

static int l_phys3d_step(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world3d", true);
  float dt = (float)luaL_checknumber(L, 2);
  LubStepInfo3d out;
  memset(&out, 0, sizeof out);
  LubStatus st = lub_phys3d_step(lgen_ctx(), world, dt, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_LubStepInfo3d(L, &out);
  return 1;
}

static int l_phys3d_pose(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body3d", true);
  LubPose3d out;
  memset(&out, 0, sizeof out);
  LubStatus st = lub_phys3d_pose(lgen_ctx(), body, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_LubPose3d(L, &out);
  return 1;
}

static int l_phys3d_pose_by_key(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world3d", true);
  LubStr key = lgen_str_arg(L, 2);
  LubPose3d out;
  memset(&out, 0, sizeof out);
  LubStatus st = lub_phys3d_pose_by_key(lgen_ctx(), world, key, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_LubPose3d(L, &out);
  return 1;
}

static int l_phys3d_velocity(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body3d", true);
  LubVelocity3d out;
  memset(&out, 0, sizeof out);
  LubStatus st = lub_phys3d_velocity(lgen_ctx(), body, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_LubVelocity3d(L, &out);
  return 1;
}

static int l_phys3d_mass(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body3d", true);
  LubMassData3d out;
  memset(&out, 0, sizeof out);
  LubStatus st = lub_phys3d_mass(lgen_ctx(), body, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_LubMassData3d(L, &out);
  return 1;
}

static int l_phys3d_center(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body3d", true);
  LubVec3d out;
  memset(&out, 0, sizeof out);
  LubStatus st = lub_phys3d_center(lgen_ctx(), body, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_LubVec3d(L, &out);
  return 1;
}

static int l_phys3d_world_point(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body3d", true);
  LubVec3d local_point_v;
  memset(&local_point_v, 0, sizeof local_point_v);
  const LubVec3d *local_point = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubVec3d(L, 2, &local_point_v);
  local_point = &local_point_v;
  LubVec3d out;
  memset(&out, 0, sizeof out);
  LubStatus st = lub_phys3d_world_point(lgen_ctx(), body, local_point, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_LubVec3d(L, &out);
  return 1;
}

static int l_phys3d_local_point(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body3d", true);
  LubVec3d world_point_v;
  memset(&world_point_v, 0, sizeof world_point_v);
  const LubVec3d *world_point = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubVec3d(L, 2, &world_point_v);
  world_point = &world_point_v;
  LubVec3d out;
  memset(&out, 0, sizeof out);
  LubStatus st = lub_phys3d_local_point(lgen_ctx(), body, world_point, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_LubVec3d(L, &out);
  return 1;
}

static int l_phys3d_velocity_at(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body3d", true);
  LubVec3d world_point_v;
  memset(&world_point_v, 0, sizeof world_point_v);
  const LubVec3d *world_point = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubVec3d(L, 2, &world_point_v);
  world_point = &world_point_v;
  LubVec3d out;
  memset(&out, 0, sizeof out);
  LubStatus st = lub_phys3d_velocity_at(lgen_ctx(), body, world_point, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_LubVec3d(L, &out);
  return 1;
}

static int l_phys3d_add_force(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body3d", true);
  LubVec3d force_v;
  memset(&force_v, 0, sizeof force_v);
  const LubVec3d *force = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubVec3d(L, 2, &force_v);
  force = &force_v;
  LubCommandOpts3d opts_v;
  memset(&opts_v, 0, sizeof opts_v);
  const LubCommandOpts3d *opts = NULL;
  if (!lua_isnoneornil(L, 3)) {
    luaL_checktype(L, 3, LUA_TTABLE);
    read_LubCommandOpts3d(L, 3, &opts_v);
    opts = &opts_v;
  }
  LubStatus st = lub_phys3d_add_force(lgen_ctx(), body, force, opts);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

static int l_phys3d_add_force_center(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body3d", true);
  LubVec3d force_v;
  memset(&force_v, 0, sizeof force_v);
  const LubVec3d *force = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubVec3d(L, 2, &force_v);
  force = &force_v;
  LubCommandOpts3d opts_v;
  memset(&opts_v, 0, sizeof opts_v);
  const LubCommandOpts3d *opts = NULL;
  if (!lua_isnoneornil(L, 3)) {
    luaL_checktype(L, 3, LUA_TTABLE);
    read_LubCommandOpts3d(L, 3, &opts_v);
    opts = &opts_v;
  }
  LubStatus st = lub_phys3d_add_force_center(lgen_ctx(), body, force, opts);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

static int l_phys3d_add_impulse(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body3d", true);
  LubVec3d impulse_v;
  memset(&impulse_v, 0, sizeof impulse_v);
  const LubVec3d *impulse = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubVec3d(L, 2, &impulse_v);
  impulse = &impulse_v;
  LubCommandOpts3d opts_v;
  memset(&opts_v, 0, sizeof opts_v);
  const LubCommandOpts3d *opts = NULL;
  if (!lua_isnoneornil(L, 3)) {
    luaL_checktype(L, 3, LUA_TTABLE);
    read_LubCommandOpts3d(L, 3, &opts_v);
    opts = &opts_v;
  }
  LubStatus st = lub_phys3d_add_impulse(lgen_ctx(), body, impulse, opts);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

static int l_phys3d_add_impulse_center(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body3d", true);
  LubVec3d impulse_v;
  memset(&impulse_v, 0, sizeof impulse_v);
  const LubVec3d *impulse = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubVec3d(L, 2, &impulse_v);
  impulse = &impulse_v;
  LubCommandOpts3d opts_v;
  memset(&opts_v, 0, sizeof opts_v);
  const LubCommandOpts3d *opts = NULL;
  if (!lua_isnoneornil(L, 3)) {
    luaL_checktype(L, 3, LUA_TTABLE);
    read_LubCommandOpts3d(L, 3, &opts_v);
    opts = &opts_v;
  }
  LubStatus st = lub_phys3d_add_impulse_center(lgen_ctx(), body, impulse, opts);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

static int l_phys3d_add_torque(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body3d", true);
  LubVec3d torque_v;
  memset(&torque_v, 0, sizeof torque_v);
  const LubVec3d *torque = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubVec3d(L, 2, &torque_v);
  torque = &torque_v;
  LubCommandOpts3d opts_v;
  memset(&opts_v, 0, sizeof opts_v);
  const LubCommandOpts3d *opts = NULL;
  if (!lua_isnoneornil(L, 3)) {
    luaL_checktype(L, 3, LUA_TTABLE);
    read_LubCommandOpts3d(L, 3, &opts_v);
    opts = &opts_v;
  }
  LubStatus st = lub_phys3d_add_torque(lgen_ctx(), body, torque, opts);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

static int l_phys3d_add_angular_impulse(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body3d", true);
  LubVec3d impulse_v;
  memset(&impulse_v, 0, sizeof impulse_v);
  const LubVec3d *impulse = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubVec3d(L, 2, &impulse_v);
  impulse = &impulse_v;
  LubCommandOpts3d opts_v;
  memset(&opts_v, 0, sizeof opts_v);
  const LubCommandOpts3d *opts = NULL;
  if (!lua_isnoneornil(L, 3)) {
    luaL_checktype(L, 3, LUA_TTABLE);
    read_LubCommandOpts3d(L, 3, &opts_v);
    opts = &opts_v;
  }
  LubStatus st =
      lub_phys3d_add_angular_impulse(lgen_ctx(), body, impulse, opts);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

static int l_phys3d_set_velocity(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body3d", true);
  LubVelocityDesc3d desc_v;
  memset(&desc_v, 0, sizeof desc_v);
  const LubVelocityDesc3d *desc = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubVelocityDesc3d(L, 2, &desc_v);
  desc = &desc_v;
  LubStatus st = lub_phys3d_set_velocity(lgen_ctx(), body, desc);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

static int l_phys3d_teleport(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body3d", true);
  LubPoseDesc3d desc_v;
  memset(&desc_v, 0, sizeof desc_v);
  const LubPoseDesc3d *desc = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubPoseDesc3d(L, 2, &desc_v);
  desc = &desc_v;
  LubStatus st = lub_phys3d_teleport(lgen_ctx(), body, desc);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

static int l_phys3d_set_target(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body3d", true);
  LubTargetDesc3d desc_v;
  memset(&desc_v, 0, sizeof desc_v);
  const LubTargetDesc3d *desc = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubTargetDesc3d(L, 2, &desc_v);
  desc = &desc_v;
  LubStatus st = lub_phys3d_set_target(lgen_ctx(), body, desc);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

static int l_phys3d_contacts(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world3d", true);
  int32_t kind_v = 0;
  const int32_t *kind = NULL;
  if (!lua_isnoneornil(L, 2)) {
    kind_v = lgen_enum_str_arg(L, 2, names_LubPhys3dEventKind,
                               values_LubPhys3dEventKind, "EventKind");
    kind = &kind_v;
  }
  const LubContactEvent3d *out = NULL;
  int32_t out_count = 0;
  LubStatus st = lub_phys3d_contacts(lgen_ctx(), world, kind, &out, &out_count);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_list_LubContactEvent3d(L, out, out_count);
  return 1;
}

static int l_phys3d_body_events(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world3d", true);
  const LubBodyEvent3d *out = NULL;
  int32_t out_count = 0;
  LubStatus st = lub_phys3d_body_events(lgen_ctx(), world, &out, &out_count);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_list_LubBodyEvent3d(L, out, out_count);
  return 1;
}

static int l_phys3d_sensors(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world3d", true);
  int32_t kind_v = 0;
  const int32_t *kind = NULL;
  if (!lua_isnoneornil(L, 2)) {
    kind_v = lgen_enum_str_arg(L, 2, names_LubPhys3dEventKind,
                               values_LubPhys3dEventKind, "EventKind");
    kind = &kind_v;
  }
  const LubSensorEvent3d *out = NULL;
  int32_t out_count = 0;
  LubStatus st = lub_phys3d_sensors(lgen_ctx(), world, kind, &out, &out_count);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_list_LubSensorEvent3d(L, out, out_count);
  return 1;
}

static int l_phys3d_joint_events(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world3d", true);
  const LubJointEvent3d *out = NULL;
  int32_t out_count = 0;
  LubStatus st = lub_phys3d_joint_events(lgen_ctx(), world, &out, &out_count);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_list_LubJointEvent3d(L, out, out_count);
  return 1;
}

static int l_phys3d_raycast(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world3d", true);
  LubRaycastDesc3d query_v;
  memset(&query_v, 0, sizeof query_v);
  const LubRaycastDesc3d *query = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubRaycastDesc3d(L, 2, &query_v);
  query = &query_v;
  LubRayHit3d out;
  memset(&out, 0, sizeof out);
  bool has = false;
  LubStatus st = lub_phys3d_raycast(lgen_ctx(), world, query, &out, &has);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (!has)
    lua_pushnil(L);
  else
    push_LubRayHit3d(L, &out);
  return 1;
}

static int l_phys3d_raycast_all(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world3d", true);
  LubRaycastDesc3d query_v;
  memset(&query_v, 0, sizeof query_v);
  const LubRaycastDesc3d *query = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubRaycastDesc3d(L, 2, &query_v);
  query = &query_v;
  LgenCallbacks *visitor_cb = lgen_callbacks_arg(L, 3);
  const LubRayHit3d *out = NULL;
  int32_t out_count = 0;
  LubStatus st = lub_phys3d_raycast_all(
      lgen_ctx(), world, query,
      visitor_cb ? tramp_l_phys3d_raycast_all_visitor : NULL, visitor_cb, &out,
      &out_count);
  if (lgen_callbacks_error(visitor_cb)) {
    lua_pushnil(L);
    lua_pushfstring(L, "phys3d_raycast_all visitor: %s",
                    lgen_callbacks_error(visitor_cb));
    lgen_callbacks_free(visitor_cb);
    lgen_release(mark);
    return 2;
  }
  lgen_callbacks_free(visitor_cb);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_list_LubRayHit3d(L, out, out_count);
  return 1;
}

static int l_phys3d_overlap_aabb(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world3d", true);
  LubAabbDesc3d query_v;
  memset(&query_v, 0, sizeof query_v);
  const LubAabbDesc3d *query = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubAabbDesc3d(L, 2, &query_v);
  query = &query_v;
  LgenCallbacks *visitor_cb = lgen_callbacks_arg(L, 3);
  const LubShapeView3d *out = NULL;
  int32_t out_count = 0;
  LubStatus st = lub_phys3d_overlap_aabb(
      lgen_ctx(), world, query,
      visitor_cb ? tramp_l_phys3d_overlap_aabb_visitor : NULL, visitor_cb, &out,
      &out_count);
  if (lgen_callbacks_error(visitor_cb)) {
    lua_pushnil(L);
    lua_pushfstring(L, "phys3d_overlap_aabb visitor: %s",
                    lgen_callbacks_error(visitor_cb));
    lgen_callbacks_free(visitor_cb);
    lgen_release(mark);
    return 2;
  }
  lgen_callbacks_free(visitor_cb);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_list_LubShapeView3d(L, out, out_count);
  return 1;
}

static int l_phys3d_overlap_shape(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world3d", true);
  LubShapeProxyDesc3d query_v;
  memset(&query_v, 0, sizeof query_v);
  const LubShapeProxyDesc3d *query = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubShapeProxyDesc3d(L, 2, &query_v);
  query = &query_v;
  LgenCallbacks *visitor_cb = lgen_callbacks_arg(L, 3);
  const LubShapeView3d *out = NULL;
  int32_t out_count = 0;
  LubStatus st = lub_phys3d_overlap_shape(
      lgen_ctx(), world, query,
      visitor_cb ? tramp_l_phys3d_overlap_shape_visitor : NULL, visitor_cb,
      &out, &out_count);
  if (lgen_callbacks_error(visitor_cb)) {
    lua_pushnil(L);
    lua_pushfstring(L, "phys3d_overlap_shape visitor: %s",
                    lgen_callbacks_error(visitor_cb));
    lgen_callbacks_free(visitor_cb);
    lgen_release(mark);
    return 2;
  }
  lgen_callbacks_free(visitor_cb);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_list_LubShapeView3d(L, out, out_count);
  return 1;
}

static int l_phys3d_shape_cast(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world3d", true);
  LubShapeProxyDesc3d query_v;
  memset(&query_v, 0, sizeof query_v);
  const LubShapeProxyDesc3d *query = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubShapeProxyDesc3d(L, 2, &query_v);
  query = &query_v;
  LubRayHit3d out;
  memset(&out, 0, sizeof out);
  bool has = false;
  LubStatus st = lub_phys3d_shape_cast(lgen_ctx(), world, query, &out, &has);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (!has)
    lua_pushnil(L);
  else
    push_LubRayHit3d(L, &out);
  return 1;
}

static int l_phys3d_shape_cast_all(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world3d", true);
  LubShapeProxyDesc3d query_v;
  memset(&query_v, 0, sizeof query_v);
  const LubShapeProxyDesc3d *query = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubShapeProxyDesc3d(L, 2, &query_v);
  query = &query_v;
  LgenCallbacks *visitor_cb = lgen_callbacks_arg(L, 3);
  const LubRayHit3d *out = NULL;
  int32_t out_count = 0;
  LubStatus st = lub_phys3d_shape_cast_all(
      lgen_ctx(), world, query,
      visitor_cb ? tramp_l_phys3d_shape_cast_all_visitor : NULL, visitor_cb,
      &out, &out_count);
  if (lgen_callbacks_error(visitor_cb)) {
    lua_pushnil(L);
    lua_pushfstring(L, "phys3d_shape_cast_all visitor: %s",
                    lgen_callbacks_error(visitor_cb));
    lgen_callbacks_free(visitor_cb);
    lgen_release(mark);
    return 2;
  }
  lgen_callbacks_free(visitor_cb);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_list_LubRayHit3d(L, out, out_count);
  return 1;
}

static int l_phys3d_body_shapes(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body3d", true);
  const LubShapeView3d *out = NULL;
  int32_t out_count = 0;
  LubStatus st = lub_phys3d_body_shapes(lgen_ctx(), body, &out, &out_count);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_list_LubShapeView3d(L, out, out_count);
  return 1;
}

static int l_phys3d_body_contacts(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle body = lgen_ref_arg(L, 1, "body3d", true);
  const LubContactData3d *out = NULL;
  int32_t out_count = 0;
  LubStatus st = lub_phys3d_body_contacts(lgen_ctx(), body, &out, &out_count);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_list_LubContactData3d(L, out, out_count);
  return 1;
}

static int l_phys3d_shape_raycast(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle shape = lgen_ref_arg(L, 1, "shape3d", true);
  LubRaycastDesc3d query_v;
  memset(&query_v, 0, sizeof query_v);
  const LubRaycastDesc3d *query = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubRaycastDesc3d(L, 2, &query_v);
  query = &query_v;
  LubShapeRayHit3d out;
  memset(&out, 0, sizeof out);
  bool has = false;
  LubStatus st = lub_phys3d_shape_raycast(lgen_ctx(), shape, query, &out, &has);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  if (!has)
    lua_pushnil(L);
  else
    push_LubShapeRayHit3d(L, &out);
  return 1;
}

static int l_phys3d_shape_closest_point(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle shape = lgen_ref_arg(L, 1, "shape3d", true);
  LubVec3d point_v;
  memset(&point_v, 0, sizeof point_v);
  const LubVec3d *point = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubVec3d(L, 2, &point_v);
  point = &point_v;
  LubVec3d out;
  memset(&out, 0, sizeof out);
  LubStatus st = lub_phys3d_shape_closest_point(lgen_ctx(), shape, point, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_LubVec3d(L, &out);
  return 1;
}

static int l_phys3d_shape_aabb(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle shape = lgen_ref_arg(L, 1, "shape3d", true);
  LubAabb3d out;
  memset(&out, 0, sizeof out);
  LubStatus st = lub_phys3d_shape_aabb(lgen_ctx(), shape, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_LubAabb3d(L, &out);
  return 1;
}

static int l_phys3d_shape_info(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle shape = lgen_ref_arg(L, 1, "shape3d", true);
  LubShapeInfo3d out;
  memset(&out, 0, sizeof out);
  LubStatus st = lub_phys3d_shape_info(lgen_ctx(), shape, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_LubShapeInfo3d(L, &out);
  return 1;
}

static int l_phys3d_shape_set_material(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle shape = lgen_ref_arg(L, 1, "shape3d", true);
  LubMaterialDesc3d desc_v;
  memset(&desc_v, 0, sizeof desc_v);
  const LubMaterialDesc3d *desc = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubMaterialDesc3d(L, 2, &desc_v);
  desc = &desc_v;
  LubStatus st = lub_phys3d_shape_set_material(lgen_ctx(), shape, desc);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

static int l_phys3d_shape_set_filter(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle shape = lgen_ref_arg(L, 1, "shape3d", true);
  LubFilterDesc3d filter_v;
  memset(&filter_v, 0, sizeof filter_v);
  const LubFilterDesc3d *filter = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubFilterDesc3d(L, 2, &filter_v);
  filter = &filter_v;
  LubStatus st = lub_phys3d_shape_set_filter(lgen_ctx(), shape, filter);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

static int l_phys3d_shape_set_events(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle shape = lgen_ref_arg(L, 1, "shape3d", true);
  LubShapeEventsDesc3d desc_v;
  memset(&desc_v, 0, sizeof desc_v);
  const LubShapeEventsDesc3d *desc = NULL;
  luaL_checktype(L, 2, LUA_TTABLE);
  read_LubShapeEventsDesc3d(L, 2, &desc_v);
  desc = &desc_v;
  LubStatus st = lub_phys3d_shape_set_events(lgen_ctx(), shape, desc);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

static int l_phys3d_profile(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world3d", true);
  LubProfile3d out;
  memset(&out, 0, sizeof out);
  LubStatus st = lub_phys3d_profile(lgen_ctx(), world, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_LubProfile3d(L, &out);
  return 1;
}

static int l_phys3d_counters(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubHandle world = lgen_ref_arg(L, 1, "world3d", true);
  LubCounters3d out;
  memset(&out, 0, sizeof out);
  LubStatus st = lub_phys3d_counters(lgen_ctx(), world, &out);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushstring(L, "not found");
    return 2;
  }
  push_LubCounters3d(L, &out);
  return 1;
}

static int l_png_load(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr path = lgen_str_arg(L, 1);
  LubView bytes = {NULL, 0, 0};
  int32_t width = 0;
  int32_t height = 0;
  int32_t format = 0;
  int32_t stride = 0;
  int32_t version = 0;
  int32_t status = 0;
  LubStr error = {NULL, 0};
  LubStatus st = lub_png_load(lgen_ctx(), path, &bytes, &width, &height,
                              &format, &stride, &version, &status, &error);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  if (st == LUB_NOT_FOUND) {
    lua_pushnil(L);
    lua_pushnil(L);
    lua_pushnil(L);
    lua_pushnil(L);
    lua_pushnil(L);
    lua_pushnil(L);
    lua_pushnil(L);
    lua_pushnil(L);
    return 8;
  }
  if (!bytes.ptr)
    lua_pushnil(L);
  else
    lgen_push_bytes_view(L, bytes);
  lua_pushinteger(L, width);
  lua_pushinteger(L, height);
  lua_pushinteger(L, format);
  lua_pushinteger(L, stride);
  lua_pushinteger(L, version);
  lua_pushstring(L, name_LubIoStatus(status) ? name_LubIoStatus(status) : "");
  if (error.len == 0 && !error.ptr)
    lua_pushnil(L);
  else
    lgen_push_str(L, error);
  return 8;
}

static int l_png_write(lua_State *L) {
  (void)L;
  LgenMark mark = lgen_mark();
  LubStr path = lgen_str_arg(L, 1);
  int32_t bytes_len = 0;
  const uint8_t *bytes = lgen_bytes_arg(L, 2, &bytes_len, true);
  int32_t width = (int32_t)luaL_checkinteger(L, 3);
  int32_t height = (int32_t)luaL_checkinteger(L, 4);
  int32_t stride_v = 0;
  const int32_t *stride = NULL;
  if (!lua_isnoneornil(L, 5)) {
    stride_v = (int32_t)luaL_checkinteger(L, 5);
    stride = &stride_v;
  }
  LubStatus st =
      lub_png_write(lgen_ctx(), path, bytes, bytes_len, width, height, stride);
  lgen_release(mark);
  if (st == LUB_ERROR)
    return lgen_raise(L);
  return 0;
}

void lub_api_gen_register(lua_State *L) {
  lua_newtable(L); // lub
  lua_pushcfunction(L, l_config);
  lua_setfield(L, -2, "config");
  lua_pushcfunction(L, l_quit);
  lua_setfield(L, -2, "quit");
  lua_newtable(L); // lub.gfx
  lua_pushcfunction(L, l_gfx_begin_pass);
  lua_setfield(L, -2, "begin_pass");
  lua_pushcfunction(L, l_gfx_end_pass);
  lua_setfield(L, -2, "end_pass");
  lua_pushcfunction(L, l_gfx_use_shader);
  lua_setfield(L, -2, "use_shader");
  lua_pushcfunction(L, l_gfx_use_shader_compute);
  lua_setfield(L, -2, "use_shader_compute");
  lua_pushcfunction(L, l_gfx_use_buffer);
  lua_setfield(L, -2, "use_buffer");
  lua_pushcfunction(L, l_gfx_use_buffer_empty);
  lua_setfield(L, -2, "use_buffer_empty");
  lua_pushcfunction(L, l_gfx_use_texture);
  lua_setfield(L, -2, "use_texture");
  lua_pushcfunction(L, l_gfx_use_texture_bytes);
  lua_setfield(L, -2, "use_texture_bytes");
  lua_pushcfunction(L, l_gfx_lookup_texture);
  lua_setfield(L, -2, "lookup_texture");
  lua_pushcfunction(L, l_gfx_lookup_shader);
  lua_setfield(L, -2, "lookup_shader");
  lua_pushcfunction(L, l_gfx_lookup_buffer);
  lua_setfield(L, -2, "lookup_buffer");
  lua_pushcfunction(L, l_gfx_resource_info);
  lua_setfield(L, -2, "resource_info");
  lua_pushcfunction(L, l_gfx_read_texture);
  lua_setfield(L, -2, "read_texture");
  lua_pushcfunction(L, l_gfx_draw);
  lua_setfield(L, -2, "draw");
  lua_pushcfunction(L, l_gfx_dispatch);
  lua_setfield(L, -2, "dispatch");
  lua_pushcfunction(L, l_gfx_size);
  lua_setfield(L, -2, "size");
  lua_pushinteger(L, 1);
  lua_setfield(L, -2, "VERTEX");
  lua_pushinteger(L, 2);
  lua_setfield(L, -2, "INDEX");
  lua_pushinteger(L, 3);
  lua_setfield(L, -2, "UNIFORM");
  lua_pushinteger(L, 4);
  lua_setfield(L, -2, "STORAGE");
  lua_pushinteger(L, 1);
  lua_setfield(L, -2, "RGBA8");
  lua_pushinteger(L, 2);
  lua_setfield(L, -2, "R8");
  lua_pushinteger(L, 3);
  lua_setfield(L, -2, "RG8");
  lua_pushinteger(L, 4);
  lua_setfield(L, -2, "R16F");
  lua_pushinteger(L, 5);
  lua_setfield(L, -2, "RG16F");
  lua_pushinteger(L, 6);
  lua_setfield(L, -2, "R32F");
  lua_pushinteger(L, 7);
  lua_setfield(L, -2, "RGBA16F");
  lua_pushinteger(L, 8);
  lua_setfield(L, -2, "RGBA32F");
  lua_pushinteger(L, 9);
  lua_setfield(L, -2, "DEPTH16");
  lua_pushinteger(L, 10);
  lua_setfield(L, -2, "DEPTH24_STENCIL8");
  lua_pushinteger(L, 11);
  lua_setfield(L, -2, "DEPTH32F");
  lua_pushinteger(L, 1);
  lua_setfield(L, -2, "CLEAR");
  lua_pushinteger(L, 2);
  lua_setfield(L, -2, "LOAD");
  lua_pushinteger(L, 3);
  lua_setfield(L, -2, "DONT_CARE");
  lua_pushinteger(L, 1);
  lua_setfield(L, -2, "STORE");
  lua_pushinteger(L, 3);
  lua_setfield(L, -2, "DONT_CARE");
  lua_pushinteger(L, 1);
  lua_setfield(L, -2, "NONE");
  lua_pushinteger(L, 2);
  lua_setfield(L, -2, "ALPHA");
  lua_pushinteger(L, 3);
  lua_setfield(L, -2, "ADDITIVE");
  lua_pushinteger(L, 4);
  lua_setfield(L, -2, "MULTIPLY");
  lua_pushinteger(L, 1);
  lua_setfield(L, -2, "NONE");
  lua_pushinteger(L, 2);
  lua_setfield(L, -2, "BACK");
  lua_pushinteger(L, 3);
  lua_setfield(L, -2, "FRONT");
  lua_pushinteger(L, 1);
  lua_setfield(L, -2, "TRIANGLES");
  lua_pushinteger(L, 2);
  lua_setfield(L, -2, "TRIANGLE_STRIP");
  lua_pushinteger(L, 3);
  lua_setfield(L, -2, "LINES");
  lua_pushinteger(L, 4);
  lua_setfield(L, -2, "LINE_STRIP");
  lua_pushinteger(L, 5);
  lua_setfield(L, -2, "POINTS");
  lua_pushinteger(L, 1);
  lua_setfield(L, -2, "LINEAR");
  lua_pushinteger(L, 2);
  lua_setfield(L, -2, "NEAREST");
  lua_pushinteger(L, 1);
  lua_setfield(L, -2, "REPEAT");
  lua_pushinteger(L, 2);
  lua_setfield(L, -2, "CLAMP");
  lua_pushstring(L, "processing");
  lua_setfield(L, -2, "PROCESSING");
  lua_pushstring(L, "ready");
  lua_setfield(L, -2, "READY");
  lua_pushstring(L, "error");
  lua_setfield(L, -2, "ERROR");
  lua_pushstring(L, "dropped");
  lua_setfield(L, -2, "DROPPED");
  lgen_push_ref(L, "texture", lub_gfx_main_tex(lgen_ctx()));
  lua_setfield(L, -2, "main_tex");
  lua_setfield(L, -2, "gfx");
  lua_newtable(L); // lub.input
  lua_pushcfunction(L, l_input_key_down);
  lua_setfield(L, -2, "key_down");
  lua_pushcfunction(L, l_input_key_pressed);
  lua_setfield(L, -2, "key_pressed");
  lua_pushcfunction(L, l_input_key_released);
  lua_setfield(L, -2, "key_released");
  lua_pushcfunction(L, l_input_mouse_down);
  lua_setfield(L, -2, "mouse_down");
  lua_pushcfunction(L, l_input_mouse_pressed);
  lua_setfield(L, -2, "mouse_pressed");
  lua_pushcfunction(L, l_input_mouse_released);
  lua_setfield(L, -2, "mouse_released");
  lua_pushcfunction(L, l_input_mouse_pos);
  lua_setfield(L, -2, "mouse_pos");
  lua_pushcfunction(L, l_input_mouse_delta);
  lua_setfield(L, -2, "mouse_delta");
  lua_setfield(L, -2, "input");
  lua_newtable(L); // lub.io
  lua_pushcfunction(L, l_io_load_text);
  lua_setfield(L, -2, "load_text");
  lua_pushcfunction(L, l_io_load_floats);
  lua_setfield(L, -2, "load_floats");
  lua_pushcfunction(L, l_io_load_gltf);
  lua_setfield(L, -2, "load_gltf");
  lua_pushcfunction(L, l_io_interleave_pn);
  lua_setfield(L, -2, "interleave_pn");
  lua_pushcfunction(L, l_io_interleave_pncm);
  lua_setfield(L, -2, "interleave_pncm");
  lua_pushcfunction(L, l_io_interleave_pncmw);
  lua_setfield(L, -2, "interleave_pncmw");
  lua_pushcfunction(L, l_io_interleave_pnu);
  lua_setfield(L, -2, "interleave_pnu");
  lua_pushcfunction(L, l_io_interleave_pnut);
  lua_setfield(L, -2, "interleave_pnut");
  lua_pushstring(L, "pending");
  lua_setfield(L, -2, "PENDING");
  lua_pushstring(L, "ready");
  lua_setfield(L, -2, "READY");
  lua_pushstring(L, "error");
  lua_setfield(L, -2, "ERROR");
  lua_setfield(L, -2, "io");
  lua_newtable(L); // lub.mesh
  lua_pushcfunction(L, l_mesh_surface_nets);
  lua_setfield(L, -2, "surface_nets");
  lua_pushcfunction(L, l_mesh_sdf_mesh);
  lua_setfield(L, -2, "sdf_mesh");
  lua_pushinteger(L, 1);
  lua_setfield(L, -2, "SPHERE");
  lua_pushinteger(L, 2);
  lua_setfield(L, -2, "BOX");
  lua_pushinteger(L, 3);
  lua_setfield(L, -2, "CAPSULE");
  lua_pushinteger(L, 4);
  lua_setfield(L, -2, "TORUS");
  lua_pushinteger(L, 5);
  lua_setfield(L, -2, "MOVE");
  lua_pushinteger(L, 6);
  lua_setfield(L, -2, "ROTATE");
  lua_pushinteger(L, 7);
  lua_setfield(L, -2, "SCALE");
  lua_pushinteger(L, 8);
  lua_setfield(L, -2, "MIRROR_X");
  lua_pushinteger(L, 9);
  lua_setfield(L, -2, "PAINT");
  lua_pushinteger(L, 10);
  lua_setfield(L, -2, "BONE");
  lua_pushinteger(L, 11);
  lua_setfield(L, -2, "UNION");
  lua_pushinteger(L, 12);
  lua_setfield(L, -2, "SMIN");
  lua_pushinteger(L, 13);
  lua_setfield(L, -2, "SUBTRACT");
  lua_pushinteger(L, 14);
  lua_setfield(L, -2, "SSUB");
  lua_pushinteger(L, 15);
  lua_setfield(L, -2, "INTERSECT");
  lua_setfield(L, -2, "mesh");
  lua_newtable(L); // lub.font
  lua_pushcfunction(L, l_font_metrics);
  lua_setfield(L, -2, "metrics");
  lua_pushcfunction(L, l_font_glyph);
  lua_setfield(L, -2, "glyph");
  lua_pushcfunction(L, l_font_glyph_mesh);
  lua_setfield(L, -2, "glyph_mesh");
  lua_pushcfunction(L, l_font_kern);
  lua_setfield(L, -2, "kern");
  lua_setfield(L, -2, "font");
  lua_newtable(L); // lub.ui
  lua_pushcfunction(L, l_ui_render);
  lua_setfield(L, -2, "render");
  lua_pushcfunction(L, l_ui_begin_window);
  lua_setfield(L, -2, "begin_window");
  lua_pushcfunction(L, l_ui_end_window);
  lua_setfield(L, -2, "end_window");
  lua_pushcfunction(L, l_ui_text);
  lua_setfield(L, -2, "text");
  lua_pushcfunction(L, l_ui_button);
  lua_setfield(L, -2, "button");
  lua_pushcfunction(L, l_ui_checkbox);
  lua_setfield(L, -2, "checkbox");
  lua_pushcfunction(L, l_ui_slider_float);
  lua_setfield(L, -2, "slider_float");
  lua_pushcfunction(L, l_ui_slider_int);
  lua_setfield(L, -2, "slider_int");
  lua_pushcfunction(L, l_ui_drag_float);
  lua_setfield(L, -2, "drag_float");
  lua_pushcfunction(L, l_ui_color_edit3);
  lua_setfield(L, -2, "color_edit3");
  lua_pushcfunction(L, l_ui_separator);
  lua_setfield(L, -2, "separator");
  lua_pushcfunction(L, l_ui_same_line);
  lua_setfield(L, -2, "same_line");
  lua_pushcfunction(L, l_ui_tree_node);
  lua_setfield(L, -2, "tree_node");
  lua_pushcfunction(L, l_ui_tree_pop);
  lua_setfield(L, -2, "tree_pop");
  lua_pushcfunction(L, l_ui_set_next_window);
  lua_setfield(L, -2, "set_next_window");
  lua_pushcfunction(L, l_ui_want_capture_mouse);
  lua_setfield(L, -2, "want_capture_mouse");
  lua_setfield(L, -2, "ui");
  lua_newtable(L); // lub.host
  lua_pushcfunction(L, l_host_available);
  lua_setfield(L, -2, "available");
  lua_pushcfunction(L, l_host_send);
  lua_setfield(L, -2, "send");
  lua_pushcfunction(L, l_host_poll);
  lua_setfield(L, -2, "poll");
  lua_setfield(L, -2, "host");
  lua_newtable(L); // lub.audio
  lua_pushcfunction(L, l_audio_snd);
  lua_setfield(L, -2, "snd");
  lua_pushcfunction(L, l_audio_snd_bytes);
  lua_setfield(L, -2, "snd_bytes");
  lua_pushcfunction(L, l_audio_decode);
  lua_setfield(L, -2, "decode");
  lua_pushcfunction(L, l_audio_play);
  lua_setfield(L, -2, "play");
  lua_pushcfunction(L, l_audio_voice);
  lua_setfield(L, -2, "voice");
  lua_pushcfunction(L, l_audio_master_volume);
  lua_setfield(L, -2, "master_volume");
  lua_pushcfunction(L, l_audio_info);
  lua_setfield(L, -2, "info");
  lua_setfield(L, -2, "audio");
  lua_newtable(L); // lub.sys
  lua_pushcfunction(L, l_sys_is_web);
  lua_setfield(L, -2, "is_web");
  lua_pushcfunction(L, l_sys_fnv1a64);
  lua_setfield(L, -2, "fnv1a64");
  lua_pushcfunction(L, l_sys_actual_fps);
  lua_setfield(L, -2, "actual_fps");
  lua_setfield(L, -2, "sys");
  lua_newtable(L); // lub.profiler
  lua_pushcfunction(L, l_profiler_enabled);
  lua_setfield(L, -2, "enabled");
  lua_pushcfunction(L, l_profiler_begin_scope);
  lua_setfield(L, -2, "begin_scope");
  lua_pushcfunction(L, l_profiler_end_scope);
  lua_setfield(L, -2, "end_scope");
  lua_pushcfunction(L, l_profiler_reset);
  lua_setfield(L, -2, "reset");
  lua_pushcfunction(L, l_profiler_report);
  lua_setfield(L, -2, "report");
  lua_setfield(L, -2, "profiler");
  lua_newtable(L); // lub.phys2d
  lua_pushcfunction(L, l_phys2d_find_world);
  lua_setfield(L, -2, "find_world");
  lua_pushcfunction(L, l_phys2d_find_body);
  lua_setfield(L, -2, "find_body");
  lua_pushcfunction(L, l_phys2d_find_shape);
  lua_setfield(L, -2, "find_shape");
  lua_pushcfunction(L, l_phys2d_find_chain);
  lua_setfield(L, -2, "find_chain");
  lua_pushcfunction(L, l_phys2d_find_joint);
  lua_setfield(L, -2, "find_joint");
  lua_pushcfunction(L, l_phys2d_world);
  lua_setfield(L, -2, "world");
  lua_pushcfunction(L, l_phys2d_begin);
  lua_setfield(L, -2, "begin");
  lua_pushcfunction(L, l_phys2d_world_info);
  lua_setfield(L, -2, "world_info");
  lua_pushcfunction(L, l_phys2d_body);
  lua_setfield(L, -2, "body");
  lua_pushcfunction(L, l_phys2d_box);
  lua_setfield(L, -2, "box");
  lua_pushcfunction(L, l_phys2d_circle);
  lua_setfield(L, -2, "circle");
  lua_pushcfunction(L, l_phys2d_capsule);
  lua_setfield(L, -2, "capsule");
  lua_pushcfunction(L, l_phys2d_segment);
  lua_setfield(L, -2, "segment");
  lua_pushcfunction(L, l_phys2d_polygon);
  lua_setfield(L, -2, "polygon");
  lua_pushcfunction(L, l_phys2d_chain);
  lua_setfield(L, -2, "chain");
  lua_pushcfunction(L, l_phys2d_chain_segments);
  lua_setfield(L, -2, "chain_segments");
  lua_pushcfunction(L, l_phys2d_joint);
  lua_setfield(L, -2, "joint");
  lua_pushcfunction(L, l_phys2d_joint_info);
  lua_setfield(L, -2, "joint_info");
  lua_pushcfunction(L, l_phys2d_joint_force);
  lua_setfield(L, -2, "joint_force");
  lua_pushcfunction(L, l_phys2d_joint_torque);
  lua_setfield(L, -2, "joint_torque");
  lua_pushcfunction(L, l_phys2d_joint_angle);
  lua_setfield(L, -2, "joint_angle");
  lua_pushcfunction(L, l_phys2d_joint_translation);
  lua_setfield(L, -2, "joint_translation");
  lua_pushcfunction(L, l_phys2d_joint_speed);
  lua_setfield(L, -2, "joint_speed");
  lua_pushcfunction(L, l_phys2d_joint_length);
  lua_setfield(L, -2, "joint_length");
  lua_pushcfunction(L, l_phys2d_joint_motor_force);
  lua_setfield(L, -2, "joint_motor_force");
  lua_pushcfunction(L, l_phys2d_joint_motor_torque);
  lua_setfield(L, -2, "joint_motor_torque");
  lua_pushcfunction(L, l_phys2d_joint_set_motor);
  lua_setfield(L, -2, "joint_set_motor");
  lua_pushcfunction(L, l_phys2d_joint_set_limit);
  lua_setfield(L, -2, "joint_set_limit");
  lua_pushcfunction(L, l_phys2d_joint_set_spring);
  lua_setfield(L, -2, "joint_set_spring");
  lua_pushcfunction(L, l_phys2d_joint_set_target);
  lua_setfield(L, -2, "joint_set_target");
  lua_pushcfunction(L, l_phys2d_step);
  lua_setfield(L, -2, "step");
  lua_pushcfunction(L, l_phys2d_pose);
  lua_setfield(L, -2, "pose");
  lua_pushcfunction(L, l_phys2d_pose_by_key);
  lua_setfield(L, -2, "pose_by_key");
  lua_pushcfunction(L, l_phys2d_velocity);
  lua_setfield(L, -2, "velocity");
  lua_pushcfunction(L, l_phys2d_mass);
  lua_setfield(L, -2, "mass");
  lua_pushcfunction(L, l_phys2d_center);
  lua_setfield(L, -2, "center");
  lua_pushcfunction(L, l_phys2d_world_point);
  lua_setfield(L, -2, "world_point");
  lua_pushcfunction(L, l_phys2d_local_point);
  lua_setfield(L, -2, "local_point");
  lua_pushcfunction(L, l_phys2d_velocity_at);
  lua_setfield(L, -2, "velocity_at");
  lua_pushcfunction(L, l_phys2d_body_shapes);
  lua_setfield(L, -2, "body_shapes");
  lua_pushcfunction(L, l_phys2d_body_joints);
  lua_setfield(L, -2, "body_joints");
  lua_pushcfunction(L, l_phys2d_body_contacts);
  lua_setfield(L, -2, "body_contacts");
  lua_pushcfunction(L, l_phys2d_shape_test_point);
  lua_setfield(L, -2, "shape_test_point");
  lua_pushcfunction(L, l_phys2d_shape_raycast);
  lua_setfield(L, -2, "shape_raycast");
  lua_pushcfunction(L, l_phys2d_shape_closest_point);
  lua_setfield(L, -2, "shape_closest_point");
  lua_pushcfunction(L, l_phys2d_shape_aabb);
  lua_setfield(L, -2, "shape_aabb");
  lua_pushcfunction(L, l_phys2d_shape_info);
  lua_setfield(L, -2, "shape_info");
  lua_pushcfunction(L, l_phys2d_shape_set_material);
  lua_setfield(L, -2, "shape_set_material");
  lua_pushcfunction(L, l_phys2d_shape_set_filter);
  lua_setfield(L, -2, "shape_set_filter");
  lua_pushcfunction(L, l_phys2d_shape_set_events);
  lua_setfield(L, -2, "shape_set_events");
  lua_pushcfunction(L, l_phys2d_contacts);
  lua_setfield(L, -2, "contacts");
  lua_pushcfunction(L, l_phys2d_body_events);
  lua_setfield(L, -2, "body_events");
  lua_pushcfunction(L, l_phys2d_sensors);
  lua_setfield(L, -2, "sensors");
  lua_pushcfunction(L, l_phys2d_raycast);
  lua_setfield(L, -2, "raycast");
  lua_pushcfunction(L, l_phys2d_raycast_all);
  lua_setfield(L, -2, "raycast_all");
  lua_pushcfunction(L, l_phys2d_overlap_aabb);
  lua_setfield(L, -2, "overlap_aabb");
  lua_pushcfunction(L, l_phys2d_shape_cast);
  lua_setfield(L, -2, "shape_cast");
  lua_pushcfunction(L, l_phys2d_shape_cast_all);
  lua_setfield(L, -2, "shape_cast_all");
  lua_pushcfunction(L, l_phys2d_cast_mover);
  lua_setfield(L, -2, "cast_mover");
  lua_pushcfunction(L, l_phys2d_collide_mover);
  lua_setfield(L, -2, "collide_mover");
  lua_pushcfunction(L, l_phys2d_explode);
  lua_setfield(L, -2, "explode");
  lua_pushcfunction(L, l_phys2d_debug);
  lua_setfield(L, -2, "debug");
  lua_pushcfunction(L, l_phys2d_profile);
  lua_setfield(L, -2, "profile");
  lua_pushcfunction(L, l_phys2d_counters);
  lua_setfield(L, -2, "counters");
  lua_pushcfunction(L, l_phys2d_add_force);
  lua_setfield(L, -2, "add_force");
  lua_pushcfunction(L, l_phys2d_add_force_center);
  lua_setfield(L, -2, "add_force_center");
  lua_pushcfunction(L, l_phys2d_add_impulse);
  lua_setfield(L, -2, "add_impulse");
  lua_pushcfunction(L, l_phys2d_add_impulse_center);
  lua_setfield(L, -2, "add_impulse_center");
  lua_pushcfunction(L, l_phys2d_add_torque);
  lua_setfield(L, -2, "add_torque");
  lua_pushcfunction(L, l_phys2d_add_angular_impulse);
  lua_setfield(L, -2, "add_angular_impulse");
  lua_pushcfunction(L, l_phys2d_set_velocity);
  lua_setfield(L, -2, "set_velocity");
  lua_pushcfunction(L, l_phys2d_teleport);
  lua_setfield(L, -2, "teleport");
  lua_pushcfunction(L, l_phys2d_set_target);
  lua_setfield(L, -2, "set_target");
  lua_pushcfunction(L, l_phys2d_set_mass_data);
  lua_setfield(L, -2, "set_mass_data");
  lua_pushinteger(L, 0);
  lua_setfield(L, -2, "STATIC");
  lua_pushinteger(L, 1);
  lua_setfield(L, -2, "KINEMATIC");
  lua_pushinteger(L, 2);
  lua_setfield(L, -2, "DYNAMIC");
  lua_pushstring(L, "box");
  lua_setfield(L, -2, "BOX");
  lua_pushstring(L, "circle");
  lua_setfield(L, -2, "CIRCLE");
  lua_pushstring(L, "capsule");
  lua_setfield(L, -2, "CAPSULE");
  lua_pushstring(L, "segment");
  lua_setfield(L, -2, "SEGMENT");
  lua_pushstring(L, "polygon");
  lua_setfield(L, -2, "POLYGON");
  lua_pushstring(L, "chain_segment");
  lua_setfield(L, -2, "CHAIN_SEGMENT");
  lua_pushstring(L, "distance");
  lua_setfield(L, -2, "DISTANCE");
  lua_pushstring(L, "filter");
  lua_setfield(L, -2, "FILTER");
  lua_pushstring(L, "motor");
  lua_setfield(L, -2, "MOTOR");
  lua_pushstring(L, "mouse");
  lua_setfield(L, -2, "MOUSE");
  lua_pushstring(L, "prismatic");
  lua_setfield(L, -2, "PRISMATIC");
  lua_pushstring(L, "revolute");
  lua_setfield(L, -2, "REVOLUTE");
  lua_pushstring(L, "weld");
  lua_setfield(L, -2, "WELD");
  lua_pushstring(L, "wheel");
  lua_setfield(L, -2, "WHEEL");
  lua_pushstring(L, "begin");
  lua_setfield(L, -2, "BEGIN");
  lua_pushstring(L, "end");
  lua_setfield(L, -2, "END");
  lua_pushstring(L, "hit");
  lua_setfield(L, -2, "HIT");
  lua_pushstring(L, "box");
  lua_setfield(L, -2, "BOX");
  lua_pushstring(L, "circle");
  lua_setfield(L, -2, "CIRCLE");
  lua_pushstring(L, "capsule");
  lua_setfield(L, -2, "CAPSULE");
  lua_pushstring(L, "segment");
  lua_setfield(L, -2, "SEGMENT");
  lua_pushstring(L, "polygon");
  lua_setfield(L, -2, "POLYGON");
  lua_setfield(L, -2, "phys2d");
  lua_newtable(L); // lub.phys3d
  lua_pushcfunction(L, l_phys3d_find_world);
  lua_setfield(L, -2, "find_world");
  lua_pushcfunction(L, l_phys3d_find_body);
  lua_setfield(L, -2, "find_body");
  lua_pushcfunction(L, l_phys3d_find_shape);
  lua_setfield(L, -2, "find_shape");
  lua_pushcfunction(L, l_phys3d_find_joint);
  lua_setfield(L, -2, "find_joint");
  lua_pushcfunction(L, l_phys3d_world);
  lua_setfield(L, -2, "world");
  lua_pushcfunction(L, l_phys3d_begin);
  lua_setfield(L, -2, "begin");
  lua_pushcfunction(L, l_phys3d_world_info);
  lua_setfield(L, -2, "world_info");
  lua_pushcfunction(L, l_phys3d_body);
  lua_setfield(L, -2, "body");
  lua_pushcfunction(L, l_phys3d_sphere);
  lua_setfield(L, -2, "sphere");
  lua_pushcfunction(L, l_phys3d_box);
  lua_setfield(L, -2, "box");
  lua_pushcfunction(L, l_phys3d_capsule);
  lua_setfield(L, -2, "capsule");
  lua_pushcfunction(L, l_phys3d_cylinder);
  lua_setfield(L, -2, "cylinder");
  lua_pushcfunction(L, l_phys3d_cone);
  lua_setfield(L, -2, "cone");
  lua_pushcfunction(L, l_phys3d_hull);
  lua_setfield(L, -2, "hull");
  lua_pushcfunction(L, l_phys3d_mesh);
  lua_setfield(L, -2, "mesh");
  lua_pushcfunction(L, l_phys3d_height_field);
  lua_setfield(L, -2, "height_field");
  lua_pushcfunction(L, l_phys3d_compound);
  lua_setfield(L, -2, "compound");
  lua_pushcfunction(L, l_phys3d_joint);
  lua_setfield(L, -2, "joint");
  lua_pushcfunction(L, l_phys3d_joint_info);
  lua_setfield(L, -2, "joint_info");
  lua_pushcfunction(L, l_phys3d_joint_force);
  lua_setfield(L, -2, "joint_force");
  lua_pushcfunction(L, l_phys3d_joint_torque);
  lua_setfield(L, -2, "joint_torque");
  lua_pushcfunction(L, l_phys3d_joint_angle);
  lua_setfield(L, -2, "joint_angle");
  lua_pushcfunction(L, l_phys3d_joint_translation);
  lua_setfield(L, -2, "joint_translation");
  lua_pushcfunction(L, l_phys3d_joint_speed);
  lua_setfield(L, -2, "joint_speed");
  lua_pushcfunction(L, l_phys3d_joint_length);
  lua_setfield(L, -2, "joint_length");
  lua_pushcfunction(L, l_phys3d_joint_motor_force);
  lua_setfield(L, -2, "joint_motor_force");
  lua_pushcfunction(L, l_phys3d_joint_motor_torque);
  lua_setfield(L, -2, "joint_motor_torque");
  lua_pushcfunction(L, l_phys3d_joint_motor_torque_vector);
  lua_setfield(L, -2, "joint_motor_torque_vector");
  lua_pushcfunction(L, l_phys3d_joint_set_motor);
  lua_setfield(L, -2, "joint_set_motor");
  lua_pushcfunction(L, l_phys3d_joint_set_limit);
  lua_setfield(L, -2, "joint_set_limit");
  lua_pushcfunction(L, l_phys3d_joint_set_spring);
  lua_setfield(L, -2, "joint_set_spring");
  lua_pushcfunction(L, l_phys3d_joint_set_target);
  lua_setfield(L, -2, "joint_set_target");
  lua_pushcfunction(L, l_phys3d_body_joints);
  lua_setfield(L, -2, "body_joints");
  lua_pushcfunction(L, l_phys3d_cast_mover);
  lua_setfield(L, -2, "cast_mover");
  lua_pushcfunction(L, l_phys3d_collide_mover);
  lua_setfield(L, -2, "collide_mover");
  lua_pushcfunction(L, l_phys3d_step);
  lua_setfield(L, -2, "step");
  lua_pushcfunction(L, l_phys3d_pose);
  lua_setfield(L, -2, "pose");
  lua_pushcfunction(L, l_phys3d_pose_by_key);
  lua_setfield(L, -2, "pose_by_key");
  lua_pushcfunction(L, l_phys3d_velocity);
  lua_setfield(L, -2, "velocity");
  lua_pushcfunction(L, l_phys3d_mass);
  lua_setfield(L, -2, "mass");
  lua_pushcfunction(L, l_phys3d_center);
  lua_setfield(L, -2, "center");
  lua_pushcfunction(L, l_phys3d_world_point);
  lua_setfield(L, -2, "world_point");
  lua_pushcfunction(L, l_phys3d_local_point);
  lua_setfield(L, -2, "local_point");
  lua_pushcfunction(L, l_phys3d_velocity_at);
  lua_setfield(L, -2, "velocity_at");
  lua_pushcfunction(L, l_phys3d_add_force);
  lua_setfield(L, -2, "add_force");
  lua_pushcfunction(L, l_phys3d_add_force_center);
  lua_setfield(L, -2, "add_force_center");
  lua_pushcfunction(L, l_phys3d_add_impulse);
  lua_setfield(L, -2, "add_impulse");
  lua_pushcfunction(L, l_phys3d_add_impulse_center);
  lua_setfield(L, -2, "add_impulse_center");
  lua_pushcfunction(L, l_phys3d_add_torque);
  lua_setfield(L, -2, "add_torque");
  lua_pushcfunction(L, l_phys3d_add_angular_impulse);
  lua_setfield(L, -2, "add_angular_impulse");
  lua_pushcfunction(L, l_phys3d_set_velocity);
  lua_setfield(L, -2, "set_velocity");
  lua_pushcfunction(L, l_phys3d_teleport);
  lua_setfield(L, -2, "teleport");
  lua_pushcfunction(L, l_phys3d_set_target);
  lua_setfield(L, -2, "set_target");
  lua_pushcfunction(L, l_phys3d_contacts);
  lua_setfield(L, -2, "contacts");
  lua_pushcfunction(L, l_phys3d_body_events);
  lua_setfield(L, -2, "body_events");
  lua_pushcfunction(L, l_phys3d_sensors);
  lua_setfield(L, -2, "sensors");
  lua_pushcfunction(L, l_phys3d_joint_events);
  lua_setfield(L, -2, "joint_events");
  lua_pushcfunction(L, l_phys3d_raycast);
  lua_setfield(L, -2, "raycast");
  lua_pushcfunction(L, l_phys3d_raycast_all);
  lua_setfield(L, -2, "raycast_all");
  lua_pushcfunction(L, l_phys3d_overlap_aabb);
  lua_setfield(L, -2, "overlap_aabb");
  lua_pushcfunction(L, l_phys3d_overlap_shape);
  lua_setfield(L, -2, "overlap_shape");
  lua_pushcfunction(L, l_phys3d_shape_cast);
  lua_setfield(L, -2, "shape_cast");
  lua_pushcfunction(L, l_phys3d_shape_cast_all);
  lua_setfield(L, -2, "shape_cast_all");
  lua_pushcfunction(L, l_phys3d_body_shapes);
  lua_setfield(L, -2, "body_shapes");
  lua_pushcfunction(L, l_phys3d_body_contacts);
  lua_setfield(L, -2, "body_contacts");
  lua_pushcfunction(L, l_phys3d_shape_raycast);
  lua_setfield(L, -2, "shape_raycast");
  lua_pushcfunction(L, l_phys3d_shape_closest_point);
  lua_setfield(L, -2, "shape_closest_point");
  lua_pushcfunction(L, l_phys3d_shape_aabb);
  lua_setfield(L, -2, "shape_aabb");
  lua_pushcfunction(L, l_phys3d_shape_info);
  lua_setfield(L, -2, "shape_info");
  lua_pushcfunction(L, l_phys3d_shape_set_material);
  lua_setfield(L, -2, "shape_set_material");
  lua_pushcfunction(L, l_phys3d_shape_set_filter);
  lua_setfield(L, -2, "shape_set_filter");
  lua_pushcfunction(L, l_phys3d_shape_set_events);
  lua_setfield(L, -2, "shape_set_events");
  lua_pushcfunction(L, l_phys3d_profile);
  lua_setfield(L, -2, "profile");
  lua_pushcfunction(L, l_phys3d_counters);
  lua_setfield(L, -2, "counters");
  lua_pushinteger(L, 0);
  lua_setfield(L, -2, "STATIC");
  lua_pushinteger(L, 1);
  lua_setfield(L, -2, "KINEMATIC");
  lua_pushinteger(L, 2);
  lua_setfield(L, -2, "DYNAMIC");
  lua_pushstring(L, "sphere");
  lua_setfield(L, -2, "SPHERE");
  lua_pushstring(L, "box");
  lua_setfield(L, -2, "BOX");
  lua_pushstring(L, "capsule");
  lua_setfield(L, -2, "CAPSULE");
  lua_pushstring(L, "cylinder");
  lua_setfield(L, -2, "CYLINDER");
  lua_pushstring(L, "cone");
  lua_setfield(L, -2, "CONE");
  lua_pushstring(L, "hull");
  lua_setfield(L, -2, "HULL");
  lua_pushstring(L, "mesh");
  lua_setfield(L, -2, "MESH");
  lua_pushstring(L, "height_field");
  lua_setfield(L, -2, "HEIGHT_FIELD");
  lua_pushstring(L, "compound");
  lua_setfield(L, -2, "COMPOUND");
  lua_pushstring(L, "distance");
  lua_setfield(L, -2, "DISTANCE");
  lua_pushstring(L, "filter");
  lua_setfield(L, -2, "FILTER");
  lua_pushstring(L, "motor");
  lua_setfield(L, -2, "MOTOR");
  lua_pushstring(L, "parallel");
  lua_setfield(L, -2, "PARALLEL");
  lua_pushstring(L, "prismatic");
  lua_setfield(L, -2, "PRISMATIC");
  lua_pushstring(L, "revolute");
  lua_setfield(L, -2, "REVOLUTE");
  lua_pushstring(L, "spherical");
  lua_setfield(L, -2, "SPHERICAL");
  lua_pushstring(L, "weld");
  lua_setfield(L, -2, "WELD");
  lua_pushstring(L, "wheel");
  lua_setfield(L, -2, "WHEEL");
  lua_pushstring(L, "begin");
  lua_setfield(L, -2, "BEGIN");
  lua_pushstring(L, "end");
  lua_setfield(L, -2, "END");
  lua_pushstring(L, "hit");
  lua_setfield(L, -2, "HIT");
  lua_setfield(L, -2, "phys3d");
  lua_newtable(L); // lub.png
  lua_pushcfunction(L, l_png_load);
  lua_setfield(L, -2, "load");
  lua_pushcfunction(L, l_png_write);
  lua_setfield(L, -2, "write");
  lua_setfield(L, -2, "png");
  lua_newtable(L); // lub.__refs
  luaL_newmetatable(L, "lub.ref.world");
  lua_createtable(L, 0, 22); // methods
  lua_pushcfunction(L, l_phys2d_find_body);
  lua_setfield(L, -2, "find_body");
  lua_pushcfunction(L, l_phys2d_find_joint);
  lua_setfield(L, -2, "find_joint");
  lua_pushcfunction(L, l_phys2d_begin);
  lua_setfield(L, -2, "begin");
  lua_pushcfunction(L, l_phys2d_world_info);
  lua_setfield(L, -2, "info");
  lua_pushcfunction(L, l_phys2d_body);
  lua_setfield(L, -2, "body");
  lua_pushcfunction(L, l_phys2d_joint);
  lua_setfield(L, -2, "joint");
  lua_pushcfunction(L, l_phys2d_step);
  lua_setfield(L, -2, "step");
  lua_pushcfunction(L, l_phys2d_pose_by_key);
  lua_setfield(L, -2, "pose_by_key");
  lua_pushcfunction(L, l_phys2d_contacts);
  lua_setfield(L, -2, "contacts");
  lua_pushcfunction(L, l_phys2d_body_events);
  lua_setfield(L, -2, "body_events");
  lua_pushcfunction(L, l_phys2d_sensors);
  lua_setfield(L, -2, "sensors");
  lua_pushcfunction(L, l_phys2d_raycast);
  lua_setfield(L, -2, "raycast");
  lua_pushcfunction(L, l_phys2d_raycast_all);
  lua_setfield(L, -2, "raycast_all");
  lua_pushcfunction(L, l_phys2d_overlap_aabb);
  lua_setfield(L, -2, "overlap_aabb");
  lua_pushcfunction(L, l_phys2d_shape_cast);
  lua_setfield(L, -2, "shape_cast");
  lua_pushcfunction(L, l_phys2d_shape_cast_all);
  lua_setfield(L, -2, "shape_cast_all");
  lua_pushcfunction(L, l_phys2d_cast_mover);
  lua_setfield(L, -2, "cast_mover");
  lua_pushcfunction(L, l_phys2d_collide_mover);
  lua_setfield(L, -2, "collide_mover");
  lua_pushcfunction(L, l_phys2d_explode);
  lua_setfield(L, -2, "explode");
  lua_pushcfunction(L, l_phys2d_debug);
  lua_setfield(L, -2, "debug");
  lua_pushcfunction(L, l_phys2d_profile);
  lua_setfield(L, -2, "profile");
  lua_pushcfunction(L, l_phys2d_counters);
  lua_setfield(L, -2, "counters");
  lua_pushvalue(L, -1);
  lua_setfield(L, -4, "world"); // lub.__refs.world
  lua_setfield(L, -2, "__index");
  lua_pop(L, 1);
  luaL_newmetatable(L, "lub.ref.body");
  lua_createtable(L, 0, 28); // methods
  lua_pushcfunction(L, l_phys2d_find_shape);
  lua_setfield(L, -2, "find_shape");
  lua_pushcfunction(L, l_phys2d_find_chain);
  lua_setfield(L, -2, "find_chain");
  lua_pushcfunction(L, l_phys2d_box);
  lua_setfield(L, -2, "box");
  lua_pushcfunction(L, l_phys2d_circle);
  lua_setfield(L, -2, "circle");
  lua_pushcfunction(L, l_phys2d_capsule);
  lua_setfield(L, -2, "capsule");
  lua_pushcfunction(L, l_phys2d_segment);
  lua_setfield(L, -2, "segment");
  lua_pushcfunction(L, l_phys2d_polygon);
  lua_setfield(L, -2, "polygon");
  lua_pushcfunction(L, l_phys2d_chain);
  lua_setfield(L, -2, "chain");
  lua_pushcfunction(L, l_phys2d_pose);
  lua_setfield(L, -2, "pose");
  lua_pushcfunction(L, l_phys2d_velocity);
  lua_setfield(L, -2, "velocity");
  lua_pushcfunction(L, l_phys2d_mass);
  lua_setfield(L, -2, "mass");
  lua_pushcfunction(L, l_phys2d_center);
  lua_setfield(L, -2, "center");
  lua_pushcfunction(L, l_phys2d_world_point);
  lua_setfield(L, -2, "world_point");
  lua_pushcfunction(L, l_phys2d_local_point);
  lua_setfield(L, -2, "local_point");
  lua_pushcfunction(L, l_phys2d_velocity_at);
  lua_setfield(L, -2, "velocity_at");
  lua_pushcfunction(L, l_phys2d_body_shapes);
  lua_setfield(L, -2, "shapes");
  lua_pushcfunction(L, l_phys2d_body_joints);
  lua_setfield(L, -2, "joints");
  lua_pushcfunction(L, l_phys2d_body_contacts);
  lua_setfield(L, -2, "contacts");
  lua_pushcfunction(L, l_phys2d_add_force);
  lua_setfield(L, -2, "add_force");
  lua_pushcfunction(L, l_phys2d_add_force_center);
  lua_setfield(L, -2, "add_force_center");
  lua_pushcfunction(L, l_phys2d_add_impulse);
  lua_setfield(L, -2, "add_impulse");
  lua_pushcfunction(L, l_phys2d_add_impulse_center);
  lua_setfield(L, -2, "add_impulse_center");
  lua_pushcfunction(L, l_phys2d_add_torque);
  lua_setfield(L, -2, "add_torque");
  lua_pushcfunction(L, l_phys2d_add_angular_impulse);
  lua_setfield(L, -2, "add_angular_impulse");
  lua_pushcfunction(L, l_phys2d_set_velocity);
  lua_setfield(L, -2, "set_velocity");
  lua_pushcfunction(L, l_phys2d_teleport);
  lua_setfield(L, -2, "teleport");
  lua_pushcfunction(L, l_phys2d_set_target);
  lua_setfield(L, -2, "set_target");
  lua_pushcfunction(L, l_phys2d_set_mass_data);
  lua_setfield(L, -2, "set_mass_data");
  lua_pushvalue(L, -1);
  lua_setfield(L, -4, "body"); // lub.__refs.body
  lua_setfield(L, -2, "__index");
  lua_pop(L, 1);
  luaL_newmetatable(L, "lub.ref.shape");
  lua_createtable(L, 0, 8); // methods
  lua_pushcfunction(L, l_phys2d_shape_test_point);
  lua_setfield(L, -2, "test_point");
  lua_pushcfunction(L, l_phys2d_shape_raycast);
  lua_setfield(L, -2, "raycast");
  lua_pushcfunction(L, l_phys2d_shape_closest_point);
  lua_setfield(L, -2, "closest_point");
  lua_pushcfunction(L, l_phys2d_shape_aabb);
  lua_setfield(L, -2, "aabb");
  lua_pushcfunction(L, l_phys2d_shape_info);
  lua_setfield(L, -2, "info");
  lua_pushcfunction(L, l_phys2d_shape_set_material);
  lua_setfield(L, -2, "set_material");
  lua_pushcfunction(L, l_phys2d_shape_set_filter);
  lua_setfield(L, -2, "set_filter");
  lua_pushcfunction(L, l_phys2d_shape_set_events);
  lua_setfield(L, -2, "set_events");
  lua_pushvalue(L, -1);
  lua_setfield(L, -4, "shape"); // lub.__refs.shape
  lua_setfield(L, -2, "__index");
  lua_pop(L, 1);
  luaL_newmetatable(L, "lub.ref.chain");
  lua_createtable(L, 0, 1); // methods
  lua_pushcfunction(L, l_phys2d_chain_segments);
  lua_setfield(L, -2, "segments");
  lua_pushvalue(L, -1);
  lua_setfield(L, -4, "chain"); // lub.__refs.chain
  lua_setfield(L, -2, "__index");
  lua_pop(L, 1);
  luaL_newmetatable(L, "lub.ref.joint");
  lua_createtable(L, 0, 13); // methods
  lua_pushcfunction(L, l_phys2d_joint_info);
  lua_setfield(L, -2, "info");
  lua_pushcfunction(L, l_phys2d_joint_force);
  lua_setfield(L, -2, "force");
  lua_pushcfunction(L, l_phys2d_joint_torque);
  lua_setfield(L, -2, "torque");
  lua_pushcfunction(L, l_phys2d_joint_angle);
  lua_setfield(L, -2, "angle");
  lua_pushcfunction(L, l_phys2d_joint_translation);
  lua_setfield(L, -2, "translation");
  lua_pushcfunction(L, l_phys2d_joint_speed);
  lua_setfield(L, -2, "speed");
  lua_pushcfunction(L, l_phys2d_joint_length);
  lua_setfield(L, -2, "length");
  lua_pushcfunction(L, l_phys2d_joint_motor_force);
  lua_setfield(L, -2, "motor_force");
  lua_pushcfunction(L, l_phys2d_joint_motor_torque);
  lua_setfield(L, -2, "motor_torque");
  lua_pushcfunction(L, l_phys2d_joint_set_motor);
  lua_setfield(L, -2, "set_motor");
  lua_pushcfunction(L, l_phys2d_joint_set_limit);
  lua_setfield(L, -2, "set_limit");
  lua_pushcfunction(L, l_phys2d_joint_set_spring);
  lua_setfield(L, -2, "set_spring");
  lua_pushcfunction(L, l_phys2d_joint_set_target);
  lua_setfield(L, -2, "set_target");
  lua_pushvalue(L, -1);
  lua_setfield(L, -4, "joint"); // lub.__refs.joint
  lua_setfield(L, -2, "__index");
  lua_pop(L, 1);
  luaL_newmetatable(L, "lub.ref.world3d");
  lua_createtable(L, 0, 22); // methods
  lua_pushcfunction(L, l_phys3d_find_body);
  lua_setfield(L, -2, "find_body");
  lua_pushcfunction(L, l_phys3d_find_joint);
  lua_setfield(L, -2, "find_joint");
  lua_pushcfunction(L, l_phys3d_begin);
  lua_setfield(L, -2, "begin");
  lua_pushcfunction(L, l_phys3d_world_info);
  lua_setfield(L, -2, "info");
  lua_pushcfunction(L, l_phys3d_body);
  lua_setfield(L, -2, "body");
  lua_pushcfunction(L, l_phys3d_joint);
  lua_setfield(L, -2, "joint");
  lua_pushcfunction(L, l_phys3d_cast_mover);
  lua_setfield(L, -2, "cast_mover");
  lua_pushcfunction(L, l_phys3d_collide_mover);
  lua_setfield(L, -2, "collide_mover");
  lua_pushcfunction(L, l_phys3d_step);
  lua_setfield(L, -2, "step");
  lua_pushcfunction(L, l_phys3d_pose_by_key);
  lua_setfield(L, -2, "pose_by_key");
  lua_pushcfunction(L, l_phys3d_contacts);
  lua_setfield(L, -2, "contacts");
  lua_pushcfunction(L, l_phys3d_body_events);
  lua_setfield(L, -2, "body_events");
  lua_pushcfunction(L, l_phys3d_sensors);
  lua_setfield(L, -2, "sensors");
  lua_pushcfunction(L, l_phys3d_joint_events);
  lua_setfield(L, -2, "joint_events");
  lua_pushcfunction(L, l_phys3d_raycast);
  lua_setfield(L, -2, "raycast");
  lua_pushcfunction(L, l_phys3d_raycast_all);
  lua_setfield(L, -2, "raycast_all");
  lua_pushcfunction(L, l_phys3d_overlap_aabb);
  lua_setfield(L, -2, "overlap_aabb");
  lua_pushcfunction(L, l_phys3d_overlap_shape);
  lua_setfield(L, -2, "overlap_shape");
  lua_pushcfunction(L, l_phys3d_shape_cast);
  lua_setfield(L, -2, "shape_cast");
  lua_pushcfunction(L, l_phys3d_shape_cast_all);
  lua_setfield(L, -2, "shape_cast_all");
  lua_pushcfunction(L, l_phys3d_profile);
  lua_setfield(L, -2, "profile");
  lua_pushcfunction(L, l_phys3d_counters);
  lua_setfield(L, -2, "counters");
  lua_pushvalue(L, -1);
  lua_setfield(L, -4, "world3d"); // lub.__refs.world3d
  lua_setfield(L, -2, "__index");
  lua_pop(L, 1);
  luaL_newmetatable(L, "lub.ref.body3d");
  lua_createtable(L, 0, 29); // methods
  lua_pushcfunction(L, l_phys3d_find_shape);
  lua_setfield(L, -2, "find_shape");
  lua_pushcfunction(L, l_phys3d_sphere);
  lua_setfield(L, -2, "sphere");
  lua_pushcfunction(L, l_phys3d_box);
  lua_setfield(L, -2, "box");
  lua_pushcfunction(L, l_phys3d_capsule);
  lua_setfield(L, -2, "capsule");
  lua_pushcfunction(L, l_phys3d_cylinder);
  lua_setfield(L, -2, "cylinder");
  lua_pushcfunction(L, l_phys3d_cone);
  lua_setfield(L, -2, "cone");
  lua_pushcfunction(L, l_phys3d_hull);
  lua_setfield(L, -2, "hull");
  lua_pushcfunction(L, l_phys3d_mesh);
  lua_setfield(L, -2, "mesh");
  lua_pushcfunction(L, l_phys3d_height_field);
  lua_setfield(L, -2, "height_field");
  lua_pushcfunction(L, l_phys3d_compound);
  lua_setfield(L, -2, "compound");
  lua_pushcfunction(L, l_phys3d_body_joints);
  lua_setfield(L, -2, "joints");
  lua_pushcfunction(L, l_phys3d_pose);
  lua_setfield(L, -2, "pose");
  lua_pushcfunction(L, l_phys3d_velocity);
  lua_setfield(L, -2, "velocity");
  lua_pushcfunction(L, l_phys3d_mass);
  lua_setfield(L, -2, "mass");
  lua_pushcfunction(L, l_phys3d_center);
  lua_setfield(L, -2, "center");
  lua_pushcfunction(L, l_phys3d_world_point);
  lua_setfield(L, -2, "world_point");
  lua_pushcfunction(L, l_phys3d_local_point);
  lua_setfield(L, -2, "local_point");
  lua_pushcfunction(L, l_phys3d_velocity_at);
  lua_setfield(L, -2, "velocity_at");
  lua_pushcfunction(L, l_phys3d_add_force);
  lua_setfield(L, -2, "add_force");
  lua_pushcfunction(L, l_phys3d_add_force_center);
  lua_setfield(L, -2, "add_force_center");
  lua_pushcfunction(L, l_phys3d_add_impulse);
  lua_setfield(L, -2, "add_impulse");
  lua_pushcfunction(L, l_phys3d_add_impulse_center);
  lua_setfield(L, -2, "add_impulse_center");
  lua_pushcfunction(L, l_phys3d_add_torque);
  lua_setfield(L, -2, "add_torque");
  lua_pushcfunction(L, l_phys3d_add_angular_impulse);
  lua_setfield(L, -2, "add_angular_impulse");
  lua_pushcfunction(L, l_phys3d_set_velocity);
  lua_setfield(L, -2, "set_velocity");
  lua_pushcfunction(L, l_phys3d_teleport);
  lua_setfield(L, -2, "teleport");
  lua_pushcfunction(L, l_phys3d_set_target);
  lua_setfield(L, -2, "set_target");
  lua_pushcfunction(L, l_phys3d_body_shapes);
  lua_setfield(L, -2, "shapes");
  lua_pushcfunction(L, l_phys3d_body_contacts);
  lua_setfield(L, -2, "contacts");
  lua_pushvalue(L, -1);
  lua_setfield(L, -4, "body3d"); // lub.__refs.body3d
  lua_setfield(L, -2, "__index");
  lua_pop(L, 1);
  luaL_newmetatable(L, "lub.ref.shape3d");
  lua_createtable(L, 0, 7); // methods
  lua_pushcfunction(L, l_phys3d_shape_raycast);
  lua_setfield(L, -2, "raycast");
  lua_pushcfunction(L, l_phys3d_shape_closest_point);
  lua_setfield(L, -2, "closest_point");
  lua_pushcfunction(L, l_phys3d_shape_aabb);
  lua_setfield(L, -2, "aabb");
  lua_pushcfunction(L, l_phys3d_shape_info);
  lua_setfield(L, -2, "info");
  lua_pushcfunction(L, l_phys3d_shape_set_material);
  lua_setfield(L, -2, "set_material");
  lua_pushcfunction(L, l_phys3d_shape_set_filter);
  lua_setfield(L, -2, "set_filter");
  lua_pushcfunction(L, l_phys3d_shape_set_events);
  lua_setfield(L, -2, "set_events");
  lua_pushvalue(L, -1);
  lua_setfield(L, -4, "shape3d"); // lub.__refs.shape3d
  lua_setfield(L, -2, "__index");
  lua_pop(L, 1);
  luaL_newmetatable(L, "lub.ref.joint3d");
  lua_createtable(L, 0, 14); // methods
  lua_pushcfunction(L, l_phys3d_joint_info);
  lua_setfield(L, -2, "info");
  lua_pushcfunction(L, l_phys3d_joint_force);
  lua_setfield(L, -2, "force");
  lua_pushcfunction(L, l_phys3d_joint_torque);
  lua_setfield(L, -2, "torque");
  lua_pushcfunction(L, l_phys3d_joint_angle);
  lua_setfield(L, -2, "angle");
  lua_pushcfunction(L, l_phys3d_joint_translation);
  lua_setfield(L, -2, "translation");
  lua_pushcfunction(L, l_phys3d_joint_speed);
  lua_setfield(L, -2, "speed");
  lua_pushcfunction(L, l_phys3d_joint_length);
  lua_setfield(L, -2, "length");
  lua_pushcfunction(L, l_phys3d_joint_motor_force);
  lua_setfield(L, -2, "motor_force");
  lua_pushcfunction(L, l_phys3d_joint_motor_torque);
  lua_setfield(L, -2, "motor_torque");
  lua_pushcfunction(L, l_phys3d_joint_motor_torque_vector);
  lua_setfield(L, -2, "motor_torque_vector");
  lua_pushcfunction(L, l_phys3d_joint_set_motor);
  lua_setfield(L, -2, "set_motor");
  lua_pushcfunction(L, l_phys3d_joint_set_limit);
  lua_setfield(L, -2, "set_limit");
  lua_pushcfunction(L, l_phys3d_joint_set_spring);
  lua_setfield(L, -2, "set_spring");
  lua_pushcfunction(L, l_phys3d_joint_set_target);
  lua_setfield(L, -2, "set_target");
  lua_pushvalue(L, -1);
  lua_setfield(L, -4, "joint3d"); // lub.__refs.joint3d
  lua_setfield(L, -2, "__index");
  lua_pop(L, 1);
  lua_setfield(L, -2, "__refs");
  lua_setglobal(L, "lub");
}
