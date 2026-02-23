-- Box2D v3 + TinyC# headless physics demo
-- Load transpiled TinyC# code (defines b2 stubs, Body, Simulation, Demo)
dofile("demos/box2d/physics.lua")

-- Overwrite b2 stub with real C module
b2 = require("b2")

-- Run the simulation (pure TinyC# logic)
print(Demo.Run())
