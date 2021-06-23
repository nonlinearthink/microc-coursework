package type;

public class IntType extends BaseType {
    private int value;

    public IntType() {
        this.value = 0;
    }

    public IntType(int value) {
        this.value = value;
    }

    public int getValue() {
        return value;
    }

    public void setValue(int value) {
        this.value = value;
    }
    
}
