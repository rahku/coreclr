add_definitions(-DFX_VER_INTERNALNAME_STR=CopyWin32Resources.exe) # what is this??

_add_executable(CopyWin32Resources
  CopyWin32Resources.cpp
  native.rc
)

target_link_libraries(CopyWin32Resources
  imagehlp.lib
  ${STATIC_MT_CRT_LIB}
  ${STATIC_MT_VCRT_LIB}
)

_install(FILES $<TARGET_FILE:CopyWin32Resources> DESTINATION uwp)
_install(FILES ${CMAKE_CURRENT_BINARY_DIR}/$<CONFIG>/CopyWin32Resources.pdb DESTINATION PDB)