"""Compile-check Lua files with a game's own LuaJIT (lua51.dll). Nothing is executed."""
import ctypes, os, sys
game_dir = sys.argv[1]
os.add_dll_directory(game_dir)
lj = ctypes.CDLL(os.path.join(game_dir, "lua51.dll"))
lj.luaL_newstate.restype = ctypes.c_void_p
lj.luaL_loadbuffer.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_size_t, ctypes.c_char_p]
lj.lua_tolstring.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p]
lj.lua_tolstring.restype = ctypes.c_char_p
lj.lua_close.argtypes = [ctypes.c_void_p]
bad = 0
for f in sys.argv[2:]:
    L = lj.luaL_newstate()
    src = open(f, "rb").read()
    rc = lj.luaL_loadbuffer(L, src, len(src), ("@" + os.path.basename(f)).encode())
    print(f, "OK" if rc == 0 else "ERROR: " + lj.lua_tolstring(L, -1, None).decode())
    bad += rc != 0
    lj.lua_close(L)
sys.exit(1 if bad else 0)
