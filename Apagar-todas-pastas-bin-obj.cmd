@echo off
setlocal enabledelayedexpansion

echo Procurando e removendo pastas "obj" e "bin" a partir de: %CD%
echo.

set /a contador=0

for /f "delims=" %%D in ('dir /ad /b /s ^| findstr /i /e "\\obj \\bin"') do (
    if exist "%%D" (
        echo Removendo: %%D
        rmdir /s /q "%%D"
        set /a contador+=1
    )
)

echo.
echo Concluido. Total de pastas removidas: %contador%
echo.
pause