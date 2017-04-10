set(CMAKE_INCLUDE_CURRENT_DIR ON)
 
set(UWPHost_SOURCES 
    UWPHost.cpp 
    HostEnvironment.cpp
    UWPHost.def
)

set(UWPHost_RESOURCES native.rc)

add_definitions(-DFX_VER_INTERNALNAME_STR=UWPHost.dll) # what is this??

_add_library(UWPHost
  SHARED
  ${UWPHost_SOURCES}
  ${UWPHost_RESOURCES}
)

target_link_libraries(UWPHost
    onecore.lib
    ${STATIC_MT_CRT_LIB}
    ${STATIC_MT_VCRT_LIB}
)

_install(FILES $<TARGET_FILE:UWPHost> DESTINATION uwp)
_install(FILES ${CMAKE_CURRENT_BINARY_DIR}/$<CONFIG>/UWPHost.pdb DESTINATION PDB)