namespace TinyCs.Tests;

public class HotReloadTests
{
    private static string RunWithRuntime(string luaCode)
    {
        var runtimePath = TestHelper.FindProjectFile("runtime/tinysystem.lua");
        var script = $"local TinySystem = dofile(\"{runtimePath}\")\n" +
                     "local HotReload = TinySystem.HotReload\n" +
                     luaCode;
        return TestHelper.RunLua(script).Trim();
    }

    [Fact]
    public void HotReload_MethodUpdated_ExistingInstanceGetsNewMethod()
    {
        // Simulate: class with method → create instance → reload with changed method
        // The instance should get the new method via metatable.
        var result = RunWithRuntime("""
            -- V1: define class and create instance
            Dog = {}
            Dog.__index = Dog
            function Dog.new(name)
              local self = setmetatable({}, Dog)
              self.Name = name
              return self
            end
            function Dog:Speak() return self.Name .. " says Woof" end

            local d = Dog.new("Rex")
            local before = d:Speak()

            -- Write V2 to temp file (method changed)
            local tmp = os.tmpname()
            local f = io.open(tmp, "w")
            f:write([[
            Dog = {}
            Dog.__index = Dog
            function Dog.new(name)
              local self = setmetatable({}, Dog)
              self.Name = name
              return self
            end
            function Dog:Speak() return self.Name .. " says Bark!" end
            ]])
            f:close()

            -- Hot reload
            HotReload.swap(tmp)
            os.remove(tmp)

            local after = d:Speak()
            print(before .. "|" .. after)
            """);
        Assert.Equal("Rex says Woof|Rex says Bark!", result);
    }

    [Fact]
    public void HotReload_NewMethodAdded()
    {
        var result = RunWithRuntime("""
            Cat = {}
            Cat.__index = Cat
            function Cat.new(name)
              local self = setmetatable({}, Cat)
              self.Name = name
              return self
            end

            local c = Cat.new("Mia")

            local tmp = os.tmpname()
            local f = io.open(tmp, "w")
            f:write([[
            Cat = {}
            Cat.__index = Cat
            function Cat.new(name)
              local self = setmetatable({}, Cat)
              self.Name = name
              return self
            end
            function Cat:Greet() return "Hi from " .. self.Name end
            ]])
            f:close()

            HotReload.swap(tmp)
            os.remove(tmp)

            print(c:Greet())
            """);
        Assert.Equal("Hi from Mia", result);
    }

    [Fact]
    public void HotReload_InstanceStatePreserved()
    {
        var result = RunWithRuntime("""
            Counter = {}
            Counter.__index = Counter
            function Counter.new()
              local self = setmetatable({}, Counter)
              self.Value = 0
              return self
            end
            function Counter:Inc() self.Value = self.Value + 1 end
            function Counter:Get() return self.Value end

            local c = Counter.new()
            c:Inc()
            c:Inc()
            c:Inc()

            local tmp = os.tmpname()
            local f = io.open(tmp, "w")
            f:write([[
            Counter = {}
            Counter.__index = Counter
            function Counter.new()
              local self = setmetatable({}, Counter)
              self.Value = 0
              return self
            end
            function Counter:Inc() self.Value = self.Value + 10 end
            function Counter:Get() return self.Value end
            ]])
            f:close()

            HotReload.swap(tmp)
            os.remove(tmp)

            -- State preserved (Value=3), new Inc adds 10
            c:Inc()
            print(c:Get())
            """);
        Assert.Equal("13", result);
    }

    [Fact]
    public void HotReload_ErrorRollback()
    {
        var result = RunWithRuntime("""
            Score = {}
            Score.Value = 42

            local tmp = os.tmpname()
            local f = io.open(tmp, "w")
            f:write("error('boom')")
            f:close()

            local ok, err = HotReload.swap(tmp)
            os.remove(tmp)

            -- Should have rolled back: Score.Value still 42
            print(tostring(ok) .. "|" .. tostring(Score.Value))
            """);
        Assert.Equal("nil|42", result);
    }

    [Fact]
    public void HotReload_MultipleClasses()
    {
        var result = RunWithRuntime("""
            A = {}
            A.__index = A
            function A.new() return setmetatable({}, A) end
            function A:Name() return "A_v1" end

            B = {}
            B.__index = B
            function B.new() return setmetatable({}, B) end
            function B:Name() return "B_v1" end

            local a = A.new()
            local b = B.new()

            local tmp = os.tmpname()
            local f = io.open(tmp, "w")
            f:write([[
            A = {}
            A.__index = A
            function A.new() return setmetatable({}, A) end
            function A:Name() return "A_v2" end
            B = {}
            B.__index = B
            function B.new() return setmetatable({}, B) end
            function B:Name() return "B_v2" end
            ]])
            f:close()

            HotReload.swap(tmp)
            os.remove(tmp)

            print(a:Name() .. "|" .. b:Name())
            """);
        Assert.Equal("A_v2|B_v2", result);
    }

    // ===== E2E: C# transpile → Lua HotReload =====

    [Fact]
    public void E2E_TranspiledClass_HotReload()
    {
        // V1: Greeter.Greet() returns "Hello"
        var luaV1 = Transpiler.Transpile("""
            public class Greeter
            {
                public string Greet() { return "Hello"; }
            }
            """);

        // V2: Greeter.Greet() returns "Hi there"
        var luaV2 = Transpiler.Transpile("""
            public class Greeter
            {
                public string Greet() { return "Hi there"; }
            }
            """);

        var runtimePath = TestHelper.FindProjectFile("runtime/tinysystem.lua");
        var script = $"""
            local TinySystem = dofile("{runtimePath}")
            local HotReload = TinySystem.HotReload

            -- Load V1
            {luaV1}
            local g = Greeter.new()
            local before = g:Greet()

            -- Write V2 to temp file and hot reload
            local tmp = os.tmpname()
            local f = io.open(tmp, "w")
            f:write([[{luaV2}]])
            f:close()

            HotReload.swap(tmp)
            os.remove(tmp)

            local after = g:Greet()
            print(before .. "|" .. after)
            """;
        var result = TestHelper.RunLua(script).Trim();
        Assert.Equal("Hello|Hi there", result);
    }

    [Fact]
    public void E2E_TranspiledClass_StatePreserved()
    {
        // V1: Counter with Inc() adding 1
        var luaV1 = Transpiler.Transpile("""
            public class Counter
            {
                public int Value;
                public Counter() { Value = 0; }
                public void Inc() { Value = Value + 1; }
                public int Get() { return Value; }
            }
            """);

        // V2: Counter with Inc() adding 10
        var luaV2 = Transpiler.Transpile("""
            public class Counter
            {
                public int Value;
                public Counter() { Value = 0; }
                public void Inc() { Value = Value + 10; }
                public int Get() { return Value; }
            }
            """);

        var runtimePath = TestHelper.FindProjectFile("runtime/tinysystem.lua");
        var script = $"""
            local TinySystem = dofile("{runtimePath}")
            local HotReload = TinySystem.HotReload

            {luaV1}
            local c = Counter.new()
            c:Inc()
            c:Inc()
            c:Inc()

            local tmp = os.tmpname()
            local f = io.open(tmp, "w")
            f:write([[{luaV2}]])
            f:close()

            HotReload.swap(tmp)
            os.remove(tmp)

            -- State preserved (Value=3), new Inc adds 10
            c:Inc()
            print(c:Get())
            """;
        var result = TestHelper.RunLua(script).Trim();
        Assert.Equal("13", result);
    }
}
