// Minimal Box2D v3 Lua binding for TinyC# demo
#include "lua.h"
#include "lauxlib.h"
#include "lualib.h"
#include "box2d/box2d.h"
#include "box2d/collision.h"
#include "box2d/math_functions.h"

// b2.createWorld({gravityX, gravityY}) -> worldId (lightuserdata-ish, stored as integers)
// We store IDs as a userdata containing the struct

// Helper: push b2WorldId as userdata
static void push_worldid(lua_State *L, b2WorldId id) {
    b2WorldId *ud = (b2WorldId *)lua_newuserdatauv(L, sizeof(b2WorldId), 0);
    *ud = id;
    luaL_setmetatable(L, "b2WorldId");
}

static b2WorldId check_worldid(lua_State *L, int idx) {
    return *(b2WorldId *)luaL_checkudata(L, idx, "b2WorldId");
}

static void push_bodyid(lua_State *L, b2BodyId id) {
    b2BodyId *ud = (b2BodyId *)lua_newuserdatauv(L, sizeof(b2BodyId), 0);
    *ud = id;
    luaL_setmetatable(L, "b2BodyId");
}

static b2BodyId check_bodyid(lua_State *L, int idx) {
    return *(b2BodyId *)luaL_checkudata(L, idx, "b2BodyId");
}

// b2.createWorld(gravityX, gravityY) -> worldId
static int l_createWorld(lua_State *L) {
    float gx = (float)luaL_checknumber(L, 1);
    float gy = (float)luaL_checknumber(L, 2);
    b2WorldDef def = b2DefaultWorldDef();
    def.gravity = (b2Vec2){gx, gy};
    b2WorldId wid = b2CreateWorld(&def);
    push_worldid(L, wid);
    return 1;
}

// b2.destroyWorld(worldId)
static int l_destroyWorld(lua_State *L) {
    b2WorldId wid = check_worldid(L, 1);
    b2DestroyWorld(wid);
    return 0;
}

// b2.createBody(worldId, type, x, y) -> bodyId
// type: 0=static, 1=kinematic, 2=dynamic
static int l_createBody(lua_State *L) {
    b2WorldId wid = check_worldid(L, 1);
    int type = (int)luaL_checkinteger(L, 2);
    float x = (float)luaL_checknumber(L, 3);
    float y = (float)luaL_checknumber(L, 4);
    b2BodyDef def = b2DefaultBodyDef();
    def.type = (b2BodyType)type;
    def.position = (b2Vec2){x, y};
    b2BodyId bid = b2CreateBody(wid, &def);
    push_bodyid(L, bid);
    return 1;
}

// b2.addBox(bodyId, halfWidth, halfHeight, density, friction, restitution)
static int l_addBox(lua_State *L) {
    b2BodyId bid = check_bodyid(L, 1);
    float hw = (float)luaL_checknumber(L, 2);
    float hh = (float)luaL_checknumber(L, 3);
    float density = (float)luaL_optnumber(L, 4, 1.0);
    float friction = (float)luaL_optnumber(L, 5, 0.3);
    float restitution = (float)luaL_optnumber(L, 6, 0.0);
    b2ShapeDef sdef = b2DefaultShapeDef();
    sdef.density = density;
    sdef.material.friction = friction;
    sdef.material.restitution = restitution;
    b2Polygon box = b2MakeBox(hw, hh);
    b2CreatePolygonShape(bid, &sdef, &box);
    return 0;
}

// b2.worldStep(worldId, timeStep, subSteps)
static int l_worldStep(lua_State *L) {
    b2WorldId wid = check_worldid(L, 1);
    float dt = (float)luaL_checknumber(L, 2);
    int substeps = (int)luaL_optinteger(L, 3, 4);
    b2World_Step(wid, dt, substeps);
    return 0;
}

// b2.getPosition(bodyId) -> x, y
static int l_getPosition(lua_State *L) {
    b2BodyId bid = check_bodyid(L, 1);
    b2Vec2 pos = b2Body_GetPosition(bid);
    lua_pushnumber(L, pos.x);
    lua_pushnumber(L, pos.y);
    return 2;
}

// b2.getRotation(bodyId) -> angle (radians)
static int l_getRotation(lua_State *L) {
    b2BodyId bid = check_bodyid(L, 1);
    b2Rot rot = b2Body_GetRotation(bid);
    float angle = b2Atan2(rot.s, rot.c);
    lua_pushnumber(L, angle);
    return 1;
}

// b2.getPositionX(bodyId) -> x
static int l_getPositionX(lua_State *L) {
    b2BodyId bid = check_bodyid(L, 1);
    b2Vec2 pos = b2Body_GetPosition(bid);
    lua_pushnumber(L, pos.x);
    return 1;
}

// b2.getPositionY(bodyId) -> y
static int l_getPositionY(lua_State *L) {
    b2BodyId bid = check_bodyid(L, 1);
    b2Vec2 pos = b2Body_GetPosition(bid);
    lua_pushnumber(L, pos.y);
    return 1;
}

// b2.getVelocity(bodyId) -> vx, vy
static int l_getVelocity(lua_State *L) {
    b2BodyId bid = check_bodyid(L, 1);
    b2Vec2 vel = b2Body_GetLinearVelocity(bid);
    lua_pushnumber(L, vel.x);
    lua_pushnumber(L, vel.y);
    return 2;
}

static const luaL_Reg b2lib[] = {
    {"createWorld", l_createWorld},
    {"destroyWorld", l_destroyWorld},
    {"createBody", l_createBody},
    {"addBox", l_addBox},
    {"worldStep", l_worldStep},
    {"getPosition", l_getPosition},
    {"getPositionX", l_getPositionX},
    {"getPositionY", l_getPositionY},
    {"getRotation", l_getRotation},
    {"getVelocity", l_getVelocity},
    {NULL, NULL}
};

int luaopen_b2(lua_State *L) {
    // Create metatables for ID types
    luaL_newmetatable(L, "b2WorldId");
    lua_pop(L, 1);
    luaL_newmetatable(L, "b2BodyId");
    lua_pop(L, 1);

    luaL_newlib(L, b2lib);

    // Body type constants
    lua_pushinteger(L, b2_staticBody);
    lua_setfield(L, -2, "STATIC");
    lua_pushinteger(L, b2_kinematicBody);
    lua_setfield(L, -2, "KINEMATIC");
    lua_pushinteger(L, b2_dynamicBody);
    lua_setfield(L, -2, "DYNAMIC");

    return 1;
}
