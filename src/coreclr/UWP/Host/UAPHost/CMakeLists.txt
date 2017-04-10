set(UAPHost_SOURCES 
    UAPHost.cpp 
)

set(UAPHost_RESOURCES native.rc)

add_definitions(-DFX_VER_INTERNALNAME_STR=UAPHost.dll) # what is this??

_add_library(UAPHost
  SHARED
  ${UAPHost_SOURCES}
  ${UAPHost_RESOURCES}
)

target_link_libraries(UAPHost
    UWPHost
    ${STATIC_MT_CRT_LIB}
    ${STATIC_MT_VCRT_LIB}
)

_install(FILES $<TARGET_FILE:UAPHost> DESTINATION uwp)
_install(FILES ${CMAKE_CURRENT_BINARY_DIR}/$<CONFIG>/UAPHost.pdb DESTINATION PDB)