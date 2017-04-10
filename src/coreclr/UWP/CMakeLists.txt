add_subdirectory(Host)
if (CLR_CMAKE_PLATFORM_ARCH_I386)
  add_subdirectory(CopyWin32Resources)
endif ()