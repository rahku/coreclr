set(CMAKE_INCLUDE_CURRENT_DIR ON)
 
set(UWPShim_SOURCES 
    UWPShim.cpp 
)

set(UWPShim_RESOURCES native.rc)

add_definitions(-DFX_VER_INTERNALNAME_STR=UWPShim.dll) # what is this??

_add_library(UWPShim
  SHARED
  ${UWPShim_SOURCES}
  ${UWPShim_RESOURCES}
)

target_link_libraries(UWPShim
    UWPHost
    ${STATIC_MT_CRT_LIB}
    ${STATIC_MT_VCRT_LIB}
)

_install(FILES $<TARGET_FILE:UWPShim> DESTINATION uwp)
_install(FILES ${CMAKE_CURRENT_BINARY_DIR}/$<CONFIG>/UWPShim.pdb DESTINATION PDB)